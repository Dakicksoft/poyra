using System.Xml.Linq;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.NestPay;

/// <summary>
/// NestPay (EST/Payten) sanal POS — İş Bankası, Ziraat, Halkbank, TEB, ING, Şekerbank vb.
/// birçok bankanın ortak altyapısı. Banka seçimi hesabın gateway_base kimlik alanıyla yapılır
/// (ör. https://sanalpos.isbank.com.tr). 3d_pay_hosting — kart bankanın sayfasında,
/// tahsilat tek adımda (satış). Canlıya çıkmadan önce banka sertifikasyon testleri zorunludur.
/// </summary>
public sealed class NestPayConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "nestpay";
    public const string HttpClientName = "poyra-nestpay";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "NestPay Sanal POS (İş, Ziraat, Halk, TEB, ING, Şeker…)",
        ConnectorType.BankVirtualPos,
        [
            new CredentialField("gateway_base", "Gateway adresi (ör. https://sanalpos.isbank.com.tr)"),
            new CredentialField("client_id", "Üye işyeri no (ClientId)"),
            new CredentialField("store_key", "3D Store Key", Secret: true),
            new CredentialField("api_name", "API kullanıcı adı"),
            new CredentialField("api_password", "API şifresi", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "3d_pay_hosting modeli: kart verisi bankada girilir, PCI kapsamı minimal.");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var gatewayBase = credentials.Require("gateway_base").TrimEnd('/');
        var storeKey = credentials.Require("store_key");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["clientid"] = credentials.Require("client_id"),
            ["storetype"] = "3d_pay_hosting",
            ["trantype"] = "Auth",
            ["hashAlgorithm"] = "ver3",
            ["amount"] = Iso4217.FormatAmount(request.AmountMinor),
            ["currency"] = Iso4217.NumericCode(request.Currency),
            ["oid"] = request.OrderId,
            ["okUrl"] = request.CallbackUrl,
            ["failUrl"] = request.CallbackUrl,
            ["lang"] = "tr",
            ["rnd"] = Guid.NewGuid().ToString("N"),
        };

        if (request.Installments > 1)
            fields["taksit"] = request.Installments.ToString();

        fields["hash"] = NestPayHash.ComputeVer3(fields, storeKey);

        return Task.FromResult(new HostedPaymentForm($"{gatewayBase}/fim/est3Dgate", fields));
    }

    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var orderId = Get(form, "oid") ?? Get(form, "ReturnOid") ?? "";

        if (!NestPayHash.ValidateCallback(form, credentials.Require("store_key")))
            return new HostedCallbackResult(false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, null, "Callback hash doğrulaması başarısız.");

        var mdStatus = Get(form, "mdStatus");
        var procCode = Get(form, "ProcReturnCode");
        var errMsg = Get(form, "ErrMsg") ?? Get(form, "mdErrorMsg");

        if (!NestPayErrorMap.IsThreeDsSuccessful(mdStatus))
            return new HostedCallbackResult(false, orderId, null, null, MaskedPan(form), null,
                UnifiedErrors.ThreeDsFailed, $"mdStatus={mdStatus}", errMsg);

        if (procCode != "00")
            return new HostedCallbackResult(false, orderId, null, Get(form, "TransId"), MaskedPan(form), null,
                NestPayErrorMap.ToUnified(procCode), procCode, errMsg);

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Get(form, "AuthCode"),
            ConnectorTxnId: Get(form, "TransId"),
            MaskedPan: MaskedPan(form),
            CardBank: Get(form, "EXTRA.CARDISSUER"),
            UnifiedCode: "",
            RawCode: procCode,
            RawMessage: null);
    }

    public async Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
    {
        var result = await Api().SendAsync(credentials.Require("gateway_base"), credentials,
            new XElement("CC5Request",
                new XElement("Type", "Void"),
                new XElement("OrderId", reference.OrderId)),
            ct);

        // Gün sonu geçtiyse banka void'i reddeder → iade yoluna işaret et
        return result is { Success: false, RawCode: "99" or "V01" }
            ? ConnectorOperationResult.Fail(UnifiedErrors.VoidWindowClosed, result.RawCode, result.RawMessage)
            : result;
    }

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => Api().SendAsync(credentials.Require("gateway_base"), credentials,
            new XElement("CC5Request",
                new XElement("Type", "Credit"),
                new XElement("OrderId", request.OrderId),
                new XElement("Total", Iso4217.FormatAmount(request.AmountMinor)),
                new XElement("Currency", Iso4217.NumericCode(request.Currency))),
            ct);

    /// <summary>
    /// Direct satış (kart Poyra'dan geçer — PCI): /fim/api Type=Auth. NON-3D işlemdir;
    /// TR'de e-ticarette 3DS fiilen standart olduğundan üretim kullanımı MOTO/istisna
    /// senaryolarıyla sınırlıdır — TODO(cert): banka onayı + işyeri non-3D yetkisi şart.
    /// </summary>
    public async Task<DirectAuthorizeResult?> AuthorizeDirectAsync(
        DirectPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var result = await Api().SendAsync(credentials.Require("gateway_base"), credentials,
            new XElement("CC5Request",
                new XElement("Type", "Auth"),
                new XElement("OrderId", request.OrderId),
                new XElement("Total", Iso4217.FormatAmount(request.AmountMinor)),
                new XElement("Currency", Iso4217.NumericCode(request.Currency)),
                new XElement("Number", request.Card.Pan),
                new XElement("Expires", $"{request.Card.ExpiryMonth:00}/{request.Card.ExpiryYear % 100:00}"),
                new XElement("Cvv2Val", request.Card.Cvv ?? ""),
                new XElement("Taksit", request.Installments > 1 ? request.Installments.ToString() : "")),
            ct);

        return result.Success
            ? new DirectAuthorizeResult(true, null, result.ConnectorTxnId,
                CardNumbers.Mask(request.Card.Pan), "", "00", null)
            : new DirectAuthorizeResult(false, null, result.ConnectorTxnId,
                CardNumbers.Mask(request.Card.Pan),
                result.UnifiedCode ?? UnifiedErrors.ProcessingError, result.RawCode, result.RawMessage);
    }

    /// <summary>
    /// Canary: var olmayan bir siparişi sorgular — banka "kayıt yok" bile dese ERİŞİLEBİLİR
    /// demektir (healthy); ağ/5xx hatası down sayılır.
    /// </summary>
    public async Task<ConnectorProbeResult?> ProbeAsync(ConnectorCredentials credentials, CancellationToken ct)
    {
        try
        {
            await Api().SendAsync(credentials.Require("gateway_base"), credentials,
                new XElement("CC5Request",
                    new XElement("OrderId", "poyra-canary-000"),
                    new XElement("Extra", new XElement("ORDERSTATUS", "QUERY"))),
                ct);
            return new ConnectorProbeResult(true);
        }
        catch (ConnectorUnavailableException ex)
        {
            return new ConnectorProbeResult(false, ex.Message);
        }
    }

    private NestPayApiClient Api() => new(httpClientFactory.CreateClient(HttpClientName));

    private static string? Get(IReadOnlyDictionary<string, string> form, string key)
        => form.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? MaskedPan(IReadOnlyDictionary<string, string> form)
        => Get(form, "MaskedPan") ?? Get(form, "md")?.Split('#').FirstOrDefault();
}
