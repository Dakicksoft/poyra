using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.AhlPay;

/// <summary>
/// <b>AHL Pay</b> ödeme kuruluşu.
///
/// <b>PCI kapsamı:</b> kart İŞYERİ tarafında toplanır ve sunucudan sunucuya iletilir;
/// banka-hosted giriş yoktur. Hosted akışta bu hesap aday listesinden düşer.
///
/// <b>Dönüş doğrulaması:</b> callback bir <c>responseHash</c> alanı taşıyor ama
/// sağlayıcının örnek yanıtında bu alan <c>null</c>'dur ve formülü belgelenmemiştir.
/// Bu yüzden dönüşe hiç güvenilmez: tahsilat, Bearer belirteçli <c>PaymentInquiry</c>
/// çağrısıyla SUNUCUDAN okunur (Moka ve Tami'deki desen).
///
/// <b>⚠ SERTİFİKASYON DURUMU:</b> istek hash formülü sağlayıcıdan alınmalı
/// (<see cref="AhlPayMessages.RequestHash"/> şu an yer tutucu).
/// </summary>
public sealed class AhlPayConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "ahlpay";
    public const string HttpClientName = "poyra-ahlpay";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "AHL Pay — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "Servis adresi (ör. https://testahlsanalpos.ahlpay.com.tr)"),
            new CredentialField("merchant_id", "Üye işyeri no (merchantId)"),
            new CredentialField("member_id", "Üye no (memberId)"),
            new CredentialField("user_code", "API kullanıcı e-postası (userCode)"),
            new CredentialField("password", "API kullanıcı şifresi", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Kart İŞYERİ formunda toplanır → PCI kapsamı; banka-hosted giriş yoktur. "
               + "Dönüş imzası belgelenmemiş (örnekte null) — tahsilat PaymentInquiry ile "
               + "sunucudan doğrulanır. TODO(cert): istek hash formülü alınmalı.");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => throw new ConnectorConfigurationException(
            "AHL Pay banka-hosted kart girişini desteklemiyor; 3DS'li direct akış kullanın.");

    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var rastgele = AhlPayMessages.Rastgele();

        using var yanit = await GonderAsync(credentials, "api/Payment/Payment3d", new
        {
            cardNumber = request.Card.Pan,
            expiryDateMonth = request.Card.ExpiryMonth.ToString("D2"),
            expiryDateYear = request.Card.ExpiryYear.ToString("D4"),
            cvv = request.Card.Cvv,
            cardHolderName = request.Card.HolderName ?? "POYRA MUSTERI",
            merchantId = Sayi(credentials.Require("merchant_id")),
            memberId = Sayi(credentials.Require("member_id")),
            userCode = credentials.Require("user_code"),
            totalAmount = AhlPayMessages.Amount(request.AmountMinor),
            txnType = "Auth",
            // Tek çekimde "0" gider (sağlayıcının örneğinde de öyle), "1" değil.
            installmentCount = request.Installments > 1 ? request.Installments.ToString() : "0",
            currency = "949",
            orderId = request.OrderId,
            rnd = rastgele,
            hash = AhlPayMessages.RequestHash(rastgele),
            description = request.Description ?? request.OrderId,
            requestIp = request.CustomerIp ?? "0.0.0.0",
            webUrl = callbackUrl,
            okUrl = callbackUrl,
            failUrl = callbackUrl,
        }, ct);

        var kok = yanit.RootElement;
        if (!Bayrak(kok, "isSuccess"))
            throw new ConnectorUnavailableException(
                $"AHL Pay 3D başlatma reddetti: {Metin(kok, "errorCode")} {Metin(kok, "message")}");

        var form = ConnectorHtml.FormuCikar(Metin(kok, "data") ?? string.Empty);
        if (form is not { } cikan)
            throw new ConnectorUnavailableException("AHL Pay 3D yanıtında beklenen form yok.");

        return new HostedPaymentForm(cikan.ActionUrl, cikan.Fields);
    }

    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, form.GetValueOrDefault("orderId", string.Empty), null, null, null, null,
            UnifiedErrors.ProcessingError, form.GetValueOrDefault("responseCode"),
            "AHL Pay dönüşü PaymentInquiry ile kesinleştirilmelidir.");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var orderId = form.GetValueOrDefault("orderId", string.Empty);
        var rastgele = AhlPayMessages.Rastgele();

        using var sorgu = await GonderAsync(credentials, "api/Payment/PaymentInquiry", new
        {
            merchantId = Sayi(credentials.Require("merchant_id")),
            memberId = Sayi(credentials.Require("member_id")),
            orderId,
            rnd = rastgele,
            hash = AhlPayMessages.RequestHash(rastgele),
        }, ct);

        var kok = sorgu.RootElement;
        var veri = kok.TryGetProperty("data", out var d) ? d : default;
        var durum = Metin(veri, "txnStatus");

        // İki koşul birden: sorgu başarılı VE işlem durumu tahsilat anlamına gelmeli.
        // Yalnız isSuccess'e bakmak, iptal edilmiş (VOID) bir işlemi başarılı saymaya
        // açık bırakırdı — sağlayıcının kendi örnek yanıtı tam olarak öyle.
        if (!Bayrak(kok, "isSuccess") || !AhlPayMessages.TahsilEdildi(durum))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                AhlPayMessages.UnifiedError(form.GetValueOrDefault("responseCode")),
                durum ?? Metin(kok, "errorCode"), Metin(kok, "message"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: form.GetValueOrDefault("authCode"),
            ConnectorTxnId: form.GetValueOrDefault("transId") ?? form.GetValueOrDefault("hostReferenceNumber"),
            MaskedPan: form.GetValueOrDefault("cardNumber"),
            CardBank: null,
            UnifiedErrors.None, durum, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, "api/Payment/Void", reference.OrderId, null, reference.ConnectorTxnId, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, "api/Payment/Refund", request.OrderId,
            AhlPayMessages.Amount(request.AmountMinor), request.ConnectorTxnId, ct);


    private async Task<ConnectorOperationResult> IslemAsync(
        ConnectorCredentials credentials, string yol, string orderId, string? tutar,
        string? txnId, CancellationToken ct)
    {
        var rastgele = AhlPayMessages.Rastgele();

        using var yanit = await GonderAsync(credentials, yol, new
        {
            merchantId = Sayi(credentials.Require("merchant_id")),
            memberId = Sayi(credentials.Require("member_id")),
            orderId,
            totalAmount = tutar,
            rnd = rastgele,
            hash = AhlPayMessages.RequestHash(rastgele),
        }, ct);

        return Bayrak(yanit.RootElement, "isSuccess")
            ? ConnectorOperationResult.Ok(txnId)
            : ConnectorOperationResult.Fail(
                AhlPayMessages.UnifiedError(Metin(yanit.RootElement, "errorCode")),
                Metin(yanit.RootElement, "errorCode"), Metin(yanit.RootElement, "message"));
    }

    private async Task<string> BelirtecAlAsync(ConnectorCredentials credentials, CancellationToken ct)
    {
        using var yanit = await GonderAsync(credentials, "api/Security/AuthenticationMerchant", new
        {
            email = credentials.Require("user_code"),
            password = credentials.Require("password"),
        }, ct, belirtecsiz: true);

        var kok = yanit.RootElement;
        var veri = kok.TryGetProperty("data", out var d) ? d : default;
        var belirtec = Metin(veri, "token") ?? Metin(kok, "token");

        return string.IsNullOrWhiteSpace(belirtec)
            ? throw new ConnectorUnavailableException("AHL Pay belirteci alınamadı.")
            : belirtec;
    }

    private async Task<JsonDocument> GonderAsync(
        ConnectorCredentials credentials, string yol, object govde, CancellationToken ct,
        bool belirtecsiz = false)
    {
        using var istek = new HttpRequestMessage(
            HttpMethod.Post, $"{credentials.Require("gateway_base").TrimEnd('/')}/{yol}")
        {
            Content = JsonContent.Create(govde),
        };

        if (!belirtecsiz)
            istek.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", await BelirtecAlAsync(credentials, ct));

        try
        {
            var istemci = httpClientFactory.CreateClient(HttpClientName);
            using var yanit = await istemci.SendAsync(istek, ct);
            var metin = await yanit.Content.ReadAsStringAsync(ct);

            if (!yanit.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"AHL Pay {yol} → {(int)yanit.StatusCode}.");

            return JsonDocument.Parse(metin);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"AHL Pay {yol} ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("AHL Pay yanıtı JSON değil.", ex);
        }
    }

    /// <summary>merchantId/memberId sayı gider — dize gönderirsek sağlayıcı 400 döner.</summary>
    private static int Sayi(string deger)
        => int.TryParse(deger, out var sayi)
            ? sayi
            : throw new ConnectorConfigurationException($"Sayısal olmayan kimlik alanı: '{deger}'.");

    private static bool Bayrak(JsonElement element, string ad)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(ad, out var deger)
           && deger.ValueKind == JsonValueKind.True;

    private static string? Metin(JsonElement element, string ad)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(ad, out var deger)
            ? deger.ValueKind switch
            {
                JsonValueKind.String => deger.GetString(),
                JsonValueKind.Number => deger.ToString(),
                _ => null,
            }
            : null;
}
