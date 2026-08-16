using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.ParamPos;

/// <summary>
/// <b>ParamPos</b> (TurkPos) ödeme kuruluşu — tek SOAP/XML konnektörümüz.
///
/// <b>PCI kapsamı:</b> kart İŞYERİ tarafında toplanır ve sunucudan sunucuya iletilir;
/// banka-hosted giriş yoktur. Hosted akışta bu hesap aday listesinden düşer.
///
/// <b>Dönüş doğrulaması:</b> callback'te imza yok — ama tahsilat zaten tarayıcı
/// dönüşüyle değil, <c>TP_WMD_Pay</c> çağrısıyla kesinleşir. Bu çağrı sunucudan
/// sunucuyadır ve sonucu (<c>Sonuc &gt; 0</c>) tek doğru kaynaktır.
///
/// <b>⚠ SERTİFİKASYON DURUMU:</b> alan adları ve sonuç kodları canlı hesapla
/// doğrulanmadan üretime alınmamalı.
/// </summary>
public sealed class ParamPosConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "parampos";
    public const string HttpClientName = "poyra-parampos";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "ParamPos (TurkPos) — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "Servis adresi (ör. https://posws.param.com.tr/turkpos.ws/service_turkpos_prod.asmx)"),
            new CredentialField("client_code", "Üye işyeri kodu (CLIENT_CODE)"),
            new CredentialField("client_username", "API kullanıcı adı (CLIENT_USERNAME)"),
            new CredentialField("client_password", "API şifresi (CLIENT_PASSWORD)", Secret: true),
            new CredentialField("guid", "İşyeri anahtarı (GUID)", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "SOAP/XML. Kart İŞYERİ formunda toplanır → PCI kapsamı; banka-hosted giriş "
               + "yoktur. Tutar VİRGÜLLÜ gider. Tahsilat TP_WMD_Pay sunucu çağrısıyla "
               + "kesinleşir, Sonuc>0 başarıdır. TODO(cert).");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => throw new ConnectorConfigurationException(
            "ParamPos banka-hosted kart girişini desteklemiyor; 3DS'li direct akış kullanın.");

    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var tutar = ParamPosMessages.Amount(request.AmountMinor);
        var taksit = Math.Max(1, request.Installments).ToString();

        var alanlar = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KK_Sahibi"] = request.Card.HolderName ?? "POYRA MUSTERI",
            ["KK_No"] = request.Card.Pan,
            ["KK_SK_Ay"] = request.Card.ExpiryMonth.ToString("D2"),
            ["KK_SK_Yil"] = request.Card.ExpiryYear.ToString("D4"),
            ["KK_CVC"] = request.Card.Cvv ?? string.Empty,
            ["KK_Sahibi_GSM"] = string.Empty,
            ["Hata_URL"] = callbackUrl,
            ["Basarili_URL"] = callbackUrl,
            ["Siparis_ID"] = request.OrderId,
            ["Siparis_Aciklama"] = request.Description ?? request.OrderId,
            ["Taksit"] = taksit,
            ["Islem_Tutar"] = tutar,
            ["Toplam_Tutar"] = tutar,
            ["Islem_Hash"] = ParamPosMessages.RequestHash(
                credentials.Require("client_code"), credentials.Require("guid"),
                taksit, tutar, tutar, request.OrderId),
            ["Islem_Guvenlik_Tip"] = "3D",
            ["Islem_ID"] = string.Empty,
            ["IPAdr"] = request.CustomerIp ?? "0.0.0.0",
            ["Ref_URL"] = callbackUrl,
        };

        var yanit = await CagirAsync(credentials, "TP_WMD_UCD", alanlar, ct);

        if (!ParamPosMessages.Basarili(yanit.GetValueOrDefault("Sonuc")))
            throw new ConnectorUnavailableException(
                $"ParamPos 3D başlatma reddetti: {yanit.GetValueOrDefault("Sonuc")} "
                + yanit.GetValueOrDefault("Sonuc_Str"));

        var html = yanit.GetValueOrDefault("UCD_HTML");
        var form = ConnectorHtml.FormuCikar(html ?? string.Empty);

        if (form is not { } cikan)
            throw new ConnectorUnavailableException("ParamPos 3D yanıtında beklenen form yok.");

        return new HostedPaymentForm(cikan.ActionUrl, cikan.Fields);
    }

    /// <summary>
    /// Callback'te imza yok; zaten tahsilat burada değil <see cref="CompleteHostedCallbackAsync"/>
    /// içindeki <c>TP_WMD_Pay</c> çağrısında kesinleşir. Bu yüzden burası asla başarı döndürmez.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, SiparisNo(form), null, null, null, null,
            ParamPosMessages.UnifiedError(null, form.GetValueOrDefault("mdStatus")),
            form.GetValueOrDefault("mdStatus"),
            "ParamPos dönüşü TP_WMD_Pay ile kesinleştirilmelidir.");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var orderId = SiparisNo(form);
        var mdStatus = form.GetValueOrDefault("mdStatus");

        if (mdStatus != "1")
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                ParamPosMessages.UnifiedError(null, mdStatus), mdStatus,
                "3D kimlik doğrulaması başarısız.");

        var yanit = await CagirAsync(credentials, "TP_WMD_Pay", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UCD_MD"] = form.GetValueOrDefault("md") ?? string.Empty,
            ["Islem_GUID"] = form.GetValueOrDefault("islemGUID") ?? string.Empty,
            ["Siparis_ID"] = orderId,
        }, ct);

        var sonuc = yanit.GetValueOrDefault("Sonuc");
        if (!ParamPosMessages.Basarili(sonuc))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                ParamPosMessages.UnifiedError(sonuc, mdStatus), sonuc,
                yanit.GetValueOrDefault("Sonuc_Str"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: yanit.GetValueOrDefault("Dekont_ID"),
            ConnectorTxnId: yanit.GetValueOrDefault("Dekont_ID") ?? form.GetValueOrDefault("islemGUID"),
            MaskedPan: null,
            CardBank: null,
            UnifiedErrors.None, sonuc, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => IptalIadeAsync(credentials, "IPTAL", reference.OrderId, "0,00", reference.ConnectorTxnId, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => IptalIadeAsync(credentials, "IADE", request.OrderId,
            ParamPosMessages.Amount(request.AmountMinor), request.ConnectorTxnId, ct);

    private async Task<ConnectorOperationResult> IptalIadeAsync(
        ConnectorCredentials credentials, string durum, string orderId, string tutar,
        string? txnId, CancellationToken ct)
    {
        var yanit = await CagirAsync(credentials, "TP_Islem_Iptal_Iade_Kismi2",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Durum"] = durum,
                ["Siparis_ID"] = orderId,
                ["Tutar"] = tutar,
            }, ct);

        var sonuc = yanit.GetValueOrDefault("Sonuc");
        return ParamPosMessages.Basarili(sonuc)
            ? ConnectorOperationResult.Ok(txnId)
            : ConnectorOperationResult.Fail(
                ParamPosMessages.UnifiedError(sonuc, null), sonuc, yanit.GetValueOrDefault("Sonuc_Str"));
    }

    private static string SiparisNo(IReadOnlyDictionary<string, string> form)
        => form.GetValueOrDefault("orderId")
           ?? form.GetValueOrDefault("Siparis_ID", string.Empty);

    private async Task<IReadOnlyDictionary<string, string>> CagirAsync(
        ConnectorCredentials credentials, string islem, Dictionary<string, string> alanlar,
        CancellationToken ct)
    {
        var zarf = ParamPosMessages.Zarf(
            islem, credentials.Require("guid"), alanlar,
            credentials.Require("client_code"), credentials.Require("client_username"),
            credentials.Require("client_password"));

        using var istek = new HttpRequestMessage(
            HttpMethod.Post, credentials.Require("gateway_base"))
        {
            Content = new StringContent(zarf, Encoding.UTF8, "text/xml"),
        };

        // SOAPAction başlığı zorunlu: olmadan sunucu hangi işlemi çağırdığımızı bilmez
        istek.Headers.TryAddWithoutValidation("SOAPAction", ParamPosMessages.Ns + islem);

        try
        {
            var istemci = httpClientFactory.CreateClient(HttpClientName);
            using var yanit = await istemci.SendAsync(istek, ct);
            var metin = await yanit.Content.ReadAsStringAsync(ct);

            if (!yanit.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"ParamPos {islem} → {(int)yanit.StatusCode}.");

            return ParamPosMessages.Oku(metin);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"ParamPos {islem} ucuna ulaşılamadı.", ex);
        }
    }
}
