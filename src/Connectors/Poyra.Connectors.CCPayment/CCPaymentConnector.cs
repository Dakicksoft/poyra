using System.Net.Http.Json;
using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.CCPayment;

/// <summary>
/// <b>CCPayment</b> ödeme kuruluşu altyapısı — Sipay, QNBPay,
///  HalkÖde aynı platformu kullanır. Sağlayıcı seçimi hesabın
/// <c>gateway_base</c> kimlik alanıyla yapılır (NestPay'de bankaları ayırdığımız gibi):
/// yedi marka için yedi ayrı adaptör yazmak, aynı hatayı yedi yerde düzeltmek olurdu.
///
/// <b>PCI kapsamı:</b> bu platform banka-hosted kart girişi sunmaz — kart İŞYERİ
/// tarafında toplanır ve sunucudan sunucuya iletilir. Poyra'da bu 3DS'li direct
/// akıştır; hosted akışta bu hesap aday listesinden düşer.
///
/// <b>⚠ SERTİFİKASYON DURUMU: sağlayıcı dokümanından yazılmadı.</b> Protokolün genel
/// şekline göre kuruldu; alan adları, imza türetmesi ve durum kodları doğrulanmadan
/// canlıya çıkamaz.
/// </summary>
public sealed class CCPaymentConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "ccpayment";
    public const string HttpClientName = "poyra-ccpayment";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "CCPayment (Sipay, QNBPay, HalkÖde) — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "Sağlayıcı adresi (ör. https://app.sipay.com.tr/ccpayment)"),
            new CredentialField("app_id", "Uygulama kimliği (app_id)"),
            new CredentialField("app_secret", "Uygulama sırrı (app_secret)", Secret: true),
            new CredentialField("merchant_key", "İşyeri anahtarı (merchant_key)", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Kart İŞYERİ formunda toplanır → PCI kapsamı. Banka-hosted giriş yoktur; "
               + "hosted akışta bu hesap atlanır. Tahsilat tarayıcı dönüşüyle DEĞİL, "
               + "/payment/complete sunucu teyidiyle kesinleşir. TODO(cert).");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => throw new ConnectorConfigurationException(
            "CCPayment banka-hosted kart girişini desteklemiyor; 3DS'li direct akış kullanın.");

    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var merchantKey = credentials.Require("merchant_key");
        var appSecret = credentials.Require("app_secret");
        var tutar = CCPaymentMessages.Amount(request.AmountMinor);
        var taksit = Math.Max(1, request.Installments);

        // İmzaya giren alanlar tutarı ve taksiti KAPSAR: kapsamasaydı müşteri 1 ₺'lik
        // işlemi 1000 ₺ gibi gönderebilir ya da taksiti değiştirebilirdi.
        var imza = CCPaymentMessages.Imzala(
            string.Join('|', tutar, taksit, ParaBirimi(request.Currency), merchantKey, request.OrderId),
            appSecret);

        var govde = new Dictionary<string, object?>
        {
            ["cc_holder_name"] = request.Card.HolderName ?? "POYRA",
            ["cc_no"] = request.Card.Pan,
            ["expiry_month"] = request.Card.ExpiryMonth.ToString("D2"),
            ["expiry_year"] = request.Card.ExpiryYear.ToString("D4"),
            ["cvv"] = request.Card.Cvv,
            ["currency_code"] = ParaBirimi(request.Currency),
            ["installments_number"] = taksit,
            ["invoice_id"] = request.OrderId,
            ["invoice_description"] = request.Description ?? request.OrderId,
            ["total"] = tutar,
            ["merchant_key"] = merchantKey,
            ["items"] = new[]
            {
                new { name = request.Description ?? "Sipariş", price = tutar, quantity = 1, description = "" },
            },
            ["name"] = "Poyra",
            ["surname"] = "Musteri",
            ["hash_key"] = imza,
            ["ip"] = request.CustomerIp ?? "0.0.0.0",
            ["transaction_type"] = "Auth",
            ["response_method"] = "POST",
            // Sonucu SUNUCU tamamlar: tarayıcının "tamamlandı" demesine güvenilmez.
            ["payment_completed_by"] = "merchant",
            ["return_url"] = callbackUrl,
            ["cancel_url"] = callbackUrl,
        };

        var yanit = await GonderAsync(credentials, "api/paySmart3D", govde, ct);
        var form = CCPaymentMessages.FormuCikar(yanit);

        if (form is not { } cikan)
            throw new ConnectorUnavailableException(
                "CCPayment 3D adımı için beklenen yönlendirme formu dönmedi.");

        return new HostedPaymentForm(cikan.ActionUrl, cikan.Fields);
    }

    /// <summary>
    /// Tarayıcı dönüşü TEK BAŞINA tahsilat kanıtı değildir: imza doğrulansa bile ödeme
    /// <see cref="CompleteHostedCallbackAsync"/> içindeki sunucu teyidiyle kesinleşir.
    /// Burası bu yüzden asla başarı döndürmez — yalnız imzayı ve 3D sonucunu değerlendirir.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var orderId = form.GetValueOrDefault("invoice_id", string.Empty);
        var mdStatus = form.GetValueOrDefault("md_status");

        if (!ImzaGecerli(form, credentials, orderId))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, mdStatus, "CCPayment imzası doğrulanamadı.");

        return new HostedCallbackResult(
            false, orderId, null, null, null, null,
            mdStatus == "1"
                ? UnifiedErrors.ProcessingError // 3D geçti ama teyit yapılmadı → henüz tahsilat yok
                : CCPaymentMessages.UnifiedError(null, mdStatus),
            mdStatus,
            form.GetValueOrDefault("error") ?? "Dönüş sunucu teyidiyle kesinleştirilmelidir.");
    }

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var orderId = form.GetValueOrDefault("invoice_id", string.Empty);
        var mdStatus = form.GetValueOrDefault("md_status");
        var providerOrderId = form.GetValueOrDefault("order_id", string.Empty);

        if (!ImzaGecerli(form, credentials, orderId))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, mdStatus, "CCPayment imzası doğrulanamadı.");

        if (mdStatus != "1")
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                CCPaymentMessages.UnifiedError(null, mdStatus), mdStatus,
                form.GetValueOrDefault("error"));

        var merchantKey = credentials.Require("merchant_key");
        var appSecret = credentials.Require("app_secret");

        var govde = new Dictionary<string, object?>
        {
            ["merchant_key"] = merchantKey,
            ["invoice_id"] = orderId,
            ["order_id"] = providerOrderId,
            ["status"] = "complete",
            ["app_lang"] = "tr",
            ["hash_key"] = CCPaymentMessages.Imzala(
                string.Join('|', merchantKey, orderId, providerOrderId, "complete"), appSecret),
        };

        var yanit = await GonderAsync(credentials, "payment/complete", govde, ct);
        using var belge = JsonDocument.Parse(yanit);
        var kok = belge.RootElement;
        var durum = Metin(kok, "status_code");

        if (durum != "100")
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                CCPaymentMessages.UnifiedError(durum, mdStatus), durum, Metin(kok, "status_description"));

        var veri = kok.TryGetProperty("data", out var d) ? d : default;
        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Metin(veri, "auth_code"),
            ConnectorTxnId: providerOrderId,
            MaskedPan: Metin(veri, "cc_no"),
            CardBank: Metin(veri, "card_bank"),
            UnifiedErrors.None, durum, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => IadeEtAsync(reference.OrderId, "0", credentials, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => IadeEtAsync(request.OrderId, CCPaymentMessages.Amount(request.AmountMinor), credentials, ct);


    private async Task<ConnectorOperationResult> IadeEtAsync(
        string orderId, string tutar, ConnectorCredentials credentials, CancellationToken ct)
    {
        var merchantKey = credentials.Require("merchant_key");

        var govde = new Dictionary<string, object?>
        {
            ["invoice_id"] = orderId,
            ["amount"] = tutar,
            ["app_id"] = credentials.Require("app_id"),
            ["app_secret"] = credentials.Require("app_secret"),
            ["merchant_key"] = merchantKey,
            ["hash_key"] = CCPaymentMessages.Imzala(
                string.Join('|', tutar, orderId, merchantKey), credentials.Require("app_secret")),
        };

        var yanit = await GonderAsync(credentials, "api/refund", govde, ct);
        using var belge = JsonDocument.Parse(yanit);
        var durum = Metin(belge.RootElement, "status_code");

        return durum == "100"
            ? ConnectorOperationResult.Ok(orderId)
            : ConnectorOperationResult.Fail(
                CCPaymentMessages.UnifiedError(durum, null), durum,
                Metin(belge.RootElement, "status_description"));
    }

    private async Task<string> BelirtecAlAsync(ConnectorCredentials credentials, CancellationToken ct)
    {
        var yanit = await GonderAsync(credentials, "api/token", new Dictionary<string, object?>
        {
            ["app_id"] = credentials.Require("app_id"),
            ["app_secret"] = credentials.Require("app_secret"),
        }, ct, belirtec: null);

        using var belge = JsonDocument.Parse(yanit);
        if (Metin(belge.RootElement, "status_code") != "100")
            throw new ConnectorUnavailableException("CCPayment belirteci alınamadı.");

        var veri = belge.RootElement.TryGetProperty("data", out var d) ? d : default;
        return Metin(veri, "token")
               ?? throw new ConnectorUnavailableException("CCPayment belirteç yanıtında token yok.");
    }

    private async Task<string> GonderAsync(
        ConnectorCredentials credentials, string yol, Dictionary<string, object?> govde,
        CancellationToken ct, string? belirtec = "")
    {
        var istemci = httpClientFactory.CreateClient(HttpClientName);
        var adres = $"{credentials.Require("gateway_base").TrimEnd('/')}/{yol}";

        using var istek = new HttpRequestMessage(HttpMethod.Post, adres)
        {
            Content = JsonContent.Create(govde),
        };

        // belirteç == "" → "gerekiyorsa al"; null → belirteç ucunun kendisi (sonsuz döngü olmasın)
        if (belirtec is not null)
            istek.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", belirtec.Length == 0 ? await BelirtecAlAsync(credentials, ct) : belirtec);

        try
        {
            using var yanit = await istemci.SendAsync(istek, ct);
            var govdeMetni = await yanit.Content.ReadAsStringAsync(ct);

            if (!yanit.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"CCPayment {yol} → {(int)yanit.StatusCode}.");

            return govdeMetni;
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"CCPayment {yol} ucuna ulaşılamadı.", ex);
        }
    }

    private static bool ImzaGecerli(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, string orderId)
    {
        var cozulmus = CCPaymentMessages.Coz(
            form.GetValueOrDefault("hash_key"), credentials.Require("app_secret"));

        // İmza çözülüyorsa anahtar bizimkiyle aynı demektir; ilk alanın sipariş numaramızı
        // tutması da başka bir işlemin imzasının buraya taşınmadığını gösterir.
        return cozulmus is not null
               && cozulmus.Split('|') is [var ilk, ..]
               && string.Equals(ilk, orderId, StringComparison.Ordinal);
    }

    private static string ParaBirimi(string currency) => currency.ToUpperInvariant();

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
