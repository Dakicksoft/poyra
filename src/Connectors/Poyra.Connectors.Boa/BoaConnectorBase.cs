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
    protected abstract string XmlKokEleman { get; }

    /// <summary>Provizyonda MD'yi saran eleman (ör. <c>KuveytTurkVPosAdditionalData</c>).</summary>
    protected abstract string XmlEkVeriEleman { get; }

    /// <summary>Gateway yolu öneki — banka kurulumuna göre değişir (ör. boş ya da <c>VirtualPOS.Gateway</c>).</summary>
    protected virtual string GatewayOnEk => string.Empty;

    protected static IReadOnlyList<CredentialField> OrtakKimlikAlanlari =>
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
        var parola = credentials.Require("password");
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
            ["HashPassword"] = BoaMessages.HashedPassword(parola),
            ["HashData"] = BoaMessages.RequestHash(
                merchantId, request.OrderId, amount, request.CallbackUrl, request.CallbackUrl,
                userName, parola),
        };

        return Task.FromResult(new HostedPaymentForm(Adres(credentials, "ThreeDModelPayGate"), fields));
    }

    /// <summary>
    /// Tarayıcı dönüşü BOA ailesinde TEK BAŞINA tahsilat kanıtı DEĞİLDİR: doğrulanacak
    /// bir dönüş imzası yoktur. Bu yüzden burası asla başarı döndürmez —
    /// tahsilat <see cref="CompleteHostedCallbackAsync"/> ile kesinleşir.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var (orderId, responseCode, message, _) = DonusuOku(form);

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
        var (orderId, responseCode, message, md) = DonusuOku(form);

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

        var govde = BoaMessages.ProvisionRequestXml(
            XmlKokEleman, XmlEkVeriEleman, merchantId, credentials.Require("customer_id"),
            userName, orderId, amount, installmentCount: 0, md,
            BoaMessages.ProvisionHash(
                merchantId, orderId, amount, userName, credentials.Require("password")));

        var provizyon = BoaMessages.Oku(
            await GonderAsync(Adres(credentials, "ThreeDModelProvisionGate"), govde, ct));
        var provizyonKodu = provizyon.GetValueOrDefault("ResponseCode");

        if (!BoaMessages.IsApprovedCode(provizyonKodu))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                BoaMessages.UnifiedError(provizyonKodu), provizyonKodu,
                provizyon.GetValueOrDefault("ResponseMessage"));

        return new HostedCallbackResult(
            true, orderId,
            provizyon.GetValueOrDefault("ProvisionNumber"),
            provizyon.GetValueOrDefault("OrderId") ?? provizyon.GetValueOrDefault("RRN"),
            provizyon.GetValueOrDefault("MaskedPan"),
            Descriptor.DisplayName,
            UnifiedErrors.None, provizyonKodu, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, "SaleReversal", reference.OrderId, reference.ConnectorTxnId,
            tutar: "0", BoaMessages.IptalXml, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, "PartialDrawBack", request.OrderId, request.ConnectorTxnId,
            BoaMessages.Amount(request.AmountMinor), BoaMessages.KismiIadeXml, ct);

    private async Task<ConnectorOperationResult> IslemAsync(
        ConnectorCredentials credentials, string uc, string merchantOrderId, string? orderId,
        string tutar,
        Func<string, string, string, string, string, string, string, string, string, string> govdeKur,
        CancellationToken ct)
    {
        var merchantId = credentials.Require("merchant_id");
        var userName = credentials.Require("user_name");
        var parola = credentials.Require("password");

        var govde = govdeKur(
            XmlKokEleman, merchantId, credentials.Require("customer_id"), userName,
            BoaMessages.HashedPassword(parola), merchantOrderId, orderId ?? string.Empty, tutar,
            BoaMessages.ProvisionHash(merchantId, merchantOrderId, tutar, userName, parola));

        var yanit = BoaMessages.Oku(await GonderAsync(Adres(credentials, uc), govde, ct));
        var kod = yanit.GetValueOrDefault("ResponseCode");

        return BoaMessages.IsApprovedCode(kod)
            ? ConnectorOperationResult.Ok(yanit.GetValueOrDefault("OrderId") ?? orderId)
            : ConnectorOperationResult.Fail(
                BoaMessages.UnifiedError(kod), kod, yanit.GetValueOrDefault("ResponseMessage"));
    }


    private string Adres(ConnectorCredentials credentials, string uc)
    {
        var taban = credentials.Require("gateway_base").TrimEnd('/');
        return GatewayOnEk.Length == 0 ? $"{taban}/Home/{uc}" : $"{taban}/{GatewayOnEk}/Home/{uc}";
    }

    private async Task<string> GonderAsync(string adres, string govde, CancellationToken ct)
    {
        try
        {
            var istemci = httpClientFactory.CreateClient(HttpClientName);
            using var yanit = await istemci.PostAsync(
                adres, new StringContent(govde, Encoding.UTF8, "text/xml"), ct);

            var metin = await yanit.Content.ReadAsStringAsync(ct);
            if (!yanit.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"BOA provizyon ucu {(int)yanit.StatusCode} döndü.");

            return metin;
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
    private static (string OrderId, string? ResponseCode, string? Message, string? Md) DonusuOku(
        IReadOnlyDictionary<string, string> form)
    {
        var alanlar = form.TryGetValue("AuthenticationResponse", out var xml) && !string.IsNullOrWhiteSpace(xml)
            ? BoaMessages.Oku(Uri.UnescapeDataString(xml))
            : form.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase);

        return (
            alanlar.GetValueOrDefault("MerchantOrderId") ?? form.GetValueOrDefault("MerchantOrderId", string.Empty),
            alanlar.GetValueOrDefault("ResponseCode"),
            alanlar.GetValueOrDefault("ResponseMessage"),
            alanlar.GetValueOrDefault("MD"));
    }
}
