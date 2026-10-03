using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Boa;

/// <summary>
/// BOA sanal POS ailesinin ortak uygulaması (Kuveyt Türk, Vakıf Katılım). Katılım
/// bankalarının çoğu aynı BOA altyapısını kullanır; bankalar arasındaki fark yalnız
/// XML kök elemanı, ek veri elemanı ve gateway adresidir.
///
/// Ortak tutulmasının sebebi somut: bu ailede tahsilat tarayıcı dönüşüyle DEĞİL,
/// dönüşteki <c>MD</c> ile yapılan <c>ThreeDModelProvisionGate</c> çağrısıyla kesinleşir.
/// Bu kural her banka için ayrı yazılsaydı, birinde unutulması tek başına parası
/// gelmemiş siparişi "ödendi" göstermeye yeterdi — nitekim bir kez öyle oldu.
///
/// <b>⚠ SERTİFİKASYON DURUMU: banka dokümanından yazılmadı.</b> Genel BOA desenine göre
/// kuruldu; alan adları, hash sırası ve dönüş kodları doğrulanmadan canlıya çıkamaz.
/// </summary>
public abstract class BoaConnectorBase(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string HttpClientName = "poyra-boa";

    public abstract string Key { get; }
    public abstract ConnectorDescriptor Descriptor { get; }

    /// <summary>Bankanın XML kök elemanı (ör. <c>KuveytTurkVPosMessage</c>).</summary>
    protected abstract string XmlRootElement { get; }

    /// <summary>Provizyonda MD'yi saran eleman (ör. <c>KuveytTurkVPosAdditionalData</c>).</summary>
    protected abstract string XmlExtraDataElement { get; }

    /// <summary>Gateway yolu öneki — banka kurulumuna göre değişir (ör. boş ya da <c>VirtualPOS.Gateway</c>).</summary>
    protected virtual string GatewayPrefix => string.Empty;

    protected static IReadOnlyList<CredentialField> CommonCredentialFields =>
    [
        new("gateway_base", "Gateway adresi (bankadan alınır)"),
        new("merchant_id", "Üye işyeri no (MerchantId)"),
        new("customer_id", "Müşteri no (CustomerId)"),
        new("user_name", "API kullanıcı adı"),
        new("password", "API şifresi", Secret: true),
    ];

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var merchantId = credentials.Require("merchant_id");
        var userName = credentials.Require("user_name");
        var password = credentials.Require("password");
        var amount = BoaMessages.Amount(request.AmountMinor);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MerchantId"] = merchantId,
            ["CustomerId"] = credentials.Require("customer_id"),
            ["UserName"] = userName,
            ["MerchantOrderId"] = request.OrderId,
            ["Amount"] = amount,
            ["CurrencyCode"] = BoaMessages.TryCurrencyCode,
            ["OkUrl"] = request.CallbackUrl,
            ["FailUrl"] = request.CallbackUrl,
            // Tek çekimde 0 gönderilir (1 değil) — banka "0" ile taksitsiz anlar.
            // TODO(cert): bu beklenti dokümanla teyit edilmeli.
            ["InstallmentCount"] = request.Installments > 1 ? request.Installments.ToString() : "0",
            ["TransactionSecurity"] = "3",
            ["HashPassword"] = BoaMessages.HashedPassword(password),
            ["HashData"] = BoaMessages.RequestHash(
                merchantId, request.OrderId, amount, request.CallbackUrl, request.CallbackUrl,
                userName, password),
        };

        return Task.FromResult(new HostedPaymentForm(Endpoint(credentials, "ThreeDModelPayGate"), fields));
    }

    /// <summary>
    /// Tarayıcı dönüşü BOA ailesinde TEK BAŞINA tahsilat kanıtı DEĞİLDİR: doğrulanacak
    /// bir dönüş imzası yoktur. Bu yüzden burası asla başarı döndürmez —
    /// tahsilat <see cref="CompleteHostedCallbackAsync"/> ile kesinleşir.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var (orderId, responseCode, message, _) = ReadReturn(form);

        return new HostedCallbackResult(
            false, orderId, null, null, null, null,
            BoaMessages.IsApprovedCode(responseCode)
                ? UnifiedErrors.ProcessingError // 3D geçti ama provizyon yapılmadı → henüz tahsilat yok
                : BoaMessages.UnifiedError(responseCode),
            responseCode,
            message ?? "Dönüş provizyon çağrısıyla kesinleştirilmelidir.");
    }

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var (orderId, responseCode, message, md) = ReadReturn(form);

        if (!BoaMessages.IsApprovedCode(responseCode))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                BoaMessages.UnifiedError(responseCode), responseCode, message);

        if (string.IsNullOrWhiteSpace(md))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, responseCode,
                "3D dönüşünde MD alanı yok — provizyon yapılamaz.");

        var amount = form.GetValueOrDefault("Amount") ?? "0";
        var merchantId = credentials.Require("merchant_id");
        var userName = credentials.Require("user_name");

        var body = BoaMessages.ProvisionRequestXml(
            XmlRootElement, XmlExtraDataElement, merchantId, credentials.Require("customer_id"),
            userName, orderId, amount, installmentCount: 0, md,
            BoaMessages.ProvisionHash(
                merchantId, orderId, amount, userName, credentials.Require("password")));

        var provision = BoaMessages.Parse(
            await SendAsync(Endpoint(credentials, "ThreeDModelProvisionGate"), body, ct));
        var provisionCode = provision.GetValueOrDefault("ResponseCode");

        if (!BoaMessages.IsApprovedCode(provisionCode))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                BoaMessages.UnifiedError(provisionCode), provisionCode,
                provision.GetValueOrDefault("ResponseMessage"));

        return new HostedCallbackResult(
            true, orderId,
            provision.GetValueOrDefault("ProvisionNumber"),
            provision.GetValueOrDefault("OrderId") ?? provision.GetValueOrDefault("RRN"),
            provision.GetValueOrDefault("MaskedPan"),
            Descriptor.DisplayName,
            UnifiedErrors.None, provisionCode, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, "SaleReversal", reference.OrderId, reference.ConnectorTxnId,
            amount: "0", BoaMessages.CancelXml, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, "PartialDrawBack", request.OrderId, request.ConnectorTxnId,
            BoaMessages.Amount(request.AmountMinor), BoaMessages.PartialRefundXml, ct);

    private async Task<ConnectorOperationResult> OperationAsync(
        ConnectorCredentials credentials, string endpoint, string merchantOrderId, string? orderId,
        string amount,
        Func<string, string, string, string, string, string, string, string, string, string> buildBody,
        CancellationToken ct)
    {
        var merchantId = credentials.Require("merchant_id");
        var userName = credentials.Require("user_name");
        var password = credentials.Require("password");

        var body = buildBody(
            XmlRootElement, merchantId, credentials.Require("customer_id"), userName,
            BoaMessages.HashedPassword(password), merchantOrderId, orderId ?? string.Empty, amount,
            BoaMessages.ProvisionHash(merchantId, merchantOrderId, amount, userName, password));

        var response = BoaMessages.Parse(await SendAsync(Endpoint(credentials, endpoint), body, ct));
        var code = response.GetValueOrDefault("ResponseCode");

        return BoaMessages.IsApprovedCode(code)
            ? ConnectorOperationResult.Ok(response.GetValueOrDefault("OrderId") ?? orderId)
            : ConnectorOperationResult.Fail(
                BoaMessages.UnifiedError(code), code, response.GetValueOrDefault("ResponseMessage"));
    }


    private string Endpoint(ConnectorCredentials credentials, string endpoint)
    {
        var baseUrl = credentials.Require("gateway_base").TrimEnd('/');
        return GatewayPrefix.Length == 0 ? $"{baseUrl}/Home/{endpoint}" : $"{baseUrl}/{GatewayPrefix}/Home/{endpoint}";
    }

    private async Task<string> SendAsync(string url, string body, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(
                url, new StringContent(body, Encoding.UTF8, "text/xml"), ct);

            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"BOA provizyon ucu {(int)response.StatusCode} döndü.");

            return text;
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException("BOA provizyon ucuna ulaşılamadı.", ex);
        }
    }

    /// <summary>
    /// 3D dönüşü ya doğrudan form alanlarında ya da URL kodlu XML taşıyan
    /// <c>AuthenticationResponse</c> alanında gelir; ikisi de okunur.
    /// </summary>
    private static (string OrderId, string? ResponseCode, string? Message, string? Md) ReadReturn(
        IReadOnlyDictionary<string, string> form)
    {
        var fields = form.TryGetValue("AuthenticationResponse", out var xml) && !string.IsNullOrWhiteSpace(xml)
            ? BoaMessages.Parse(Uri.UnescapeDataString(xml))
            : form.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase);

        return (
            fields.GetValueOrDefault("MerchantOrderId") ?? form.GetValueOrDefault("MerchantOrderId", string.Empty),
            fields.GetValueOrDefault("ResponseCode"),
            fields.GetValueOrDefault("ResponseMessage"),
            fields.GetValueOrDefault("MD"));
    }
}
