using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.PayFlex;

/// <summary>
/// VakıfBank PayFlex (VPOS 7/24) — Ortak Ödeme Sayfası (CPP) modeli:
/// 1. sunucudan RegisterPayment → PaymentToken + sayfa adresi,
/// 2. müşteri banka sayfasına GET ile gider (Ptkn), kartı ORADA girer,
/// 3. banka SuccessUrl/FailUrl'e döner; sonuç redirect parametresine GÜVENİLMEZ —
///    CompleteHostedCallbackAsync sunucudan GetPaymentResult ile doğrular.
/// Uç/alan adları banka sertifikasyonunda doğrulanmadan canlıya çıkılmaz (TODO-cert).
/// </summary>
public sealed class PayFlexConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "payflex";
    public const string HttpClientName = "poyra-payflex";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "VakıfBank PayFlex — Ortak Ödeme Sayfası",
        ConnectorType.BankVirtualPos,
        [
            new CredentialField("gateway_base", "Gateway adresi (ör. https://onlineodeme.vakifbank.com.tr:4443)"),
            new CredentialField("merchant_id", "Üye işyeri no (MerchantId)"),
            new CredentialField("password", "İşyeri şifresi", Secret: true),
            new CredentialField("terminal_no", "Terminal No (V…)"),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Kart banka sayfasında girilir; sonuç redirect'ten değil sunucu sorgusuyla doğrulanır.");

    public async Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var xml = PayFlexMessages.BuildRegisterPaymentXml(credentials, request);
        var body = await PostAsync(credentials, "/UIService/RegisterPayment", xml, ct); // TODO(cert): uç adı

        PayFlexRegisterResult result;
        try
        {
            result = PayFlexMessages.ParseRegisterResponse(body);
        }
        catch (Exception)
        {
            throw new ConnectorUnavailableException("PayFlex RegisterPayment yanıtı çözümlenemedi.");
        }

        if (result.ResultCode != "0000" || result.PaymentToken is null || result.CommonPaymentUrl is null)
            throw new ConnectorUnavailableException(
                $"PayFlex kayıt reddi: {result.ResultCode} {result.ResultDetail}");

        // Müşteri tarayıcısı GET ile ortak ödeme sayfasına gider (Ptkn query'de)
        return new HostedPaymentForm(
            result.CommonPaymentUrl,
            new Dictionary<string, string> { ["Ptkn"] = result.PaymentToken },
            Method: "GET");
    }

    /// <summary>Senkron yol bilinçli olarak kapalıdır — CPP sonucu redirect'ten okunmaz.</summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, Get(form, "Ptkn") ?? "", null, null, null, null,
            UnifiedErrors.SignatureInvalid, null,
            "PayFlex sonucu sunucu sorgusuyla doğrulanır (CompleteHostedCallbackAsync).");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var token = Get(form, "Ptkn") ?? Get(form, "PaymentToken");
        if (string.IsNullOrEmpty(token))
            return new HostedCallbackResult(false, "", null, null, null, null,
                UnifiedErrors.SignatureInvalid, null, "PayFlex dönüşünde Ptkn yok.");

        // Kaynak-of-truth: bankaya sor — redirect parametreleri kurcalanabilir
        var xml = PayFlexMessages.BuildInquiryXml(credentials, token);
        var body = await PostAsync(credentials, "/UIService/GetPaymentResult", xml, ct); // TODO(cert): uç adı

        PayFlexPaymentResult result;
        try
        {
            result = PayFlexMessages.ParsePaymentResult(body);
        }
        catch (Exception)
        {
            return new HostedCallbackResult(false, "", null, null, null, null,
                UnifiedErrors.ProcessingError, null, "PayFlex sonuç yanıtı çözümlenemedi.");
        }

        var orderId = result.OrderId ?? "";
        return result.ResultCode == "0000"
            ? new HostedCallbackResult(true, orderId, result.AuthCode, result.TransactionId,
                result.MaskedPan, "VakıfBank", "", result.ResultCode, null)
            : new HostedCallbackResult(false, orderId, null, result.TransactionId, result.MaskedPan, null,
                PayFlexMessages.ToUnified(result.ResultCode), result.ResultCode, result.ResultDetail);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => OperateAsync(credentials, "Cancel", reference.ConnectorTxnId, null, "TRY", ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => OperateAsync(credentials, "Refund", request.ConnectorTxnId, request.AmountMinor, request.Currency, ct);

    private async Task<ConnectorOperationResult> OperateAsync(
        ConnectorCredentials credentials, string type, string? referenceTransactionId,
        long? amountMinor, string currency, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(referenceTransactionId))
            return ConnectorOperationResult.Fail(UnifiedErrors.ProcessingError, null,
                "PayFlex iptal/iade için TransactionId (ReferenceTransactionId) gerekli.");

        var xml = PayFlexMessages.BuildOperationXml(credentials, type, referenceTransactionId, amountMinor, currency);
        var body = await PostAsync(credentials, "/VposService/v3/Vposreq.aspx", xml, ct); // TODO(cert): uç adı

        PayFlexPaymentResult result;
        try
        {
            result = PayFlexMessages.ParsePaymentResult(body);
        }
        catch (Exception)
        {
            return ConnectorOperationResult.Fail(UnifiedErrors.ProcessingError, null, "Geçersiz PayFlex yanıtı");
        }

        return result.ResultCode == "0000"
            ? ConnectorOperationResult.Ok(result.TransactionId)
            : ConnectorOperationResult.Fail(
                PayFlexMessages.ToUnified(result.ResultCode), result.ResultCode, result.ResultDetail);
    }

    private async Task<string> PostAsync(
        ConnectorCredentials credentials, string path, string xml, CancellationToken ct)
    {
        var gatewayBase = credentials.Require("gateway_base").TrimEnd('/');
        using var content = new StringContent(
            "prmstr=" + Uri.EscapeDataString(xml), Encoding.UTF8, "application/x-www-form-urlencoded");

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName)
                .PostAsync(gatewayBase + path, content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorUnavailableException($"PayFlex'e ulaşılamadı: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
            throw new ConnectorUnavailableException($"PayFlex HTTP {(int)response.StatusCode}");

        return await response.Content.ReadAsStringAsync(ct);
    }

    private static string? Get(IReadOnlyDictionary<string, string> form, string key)
        => form.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
}
