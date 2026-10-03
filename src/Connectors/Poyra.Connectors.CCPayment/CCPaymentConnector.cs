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
        var amount = CCPaymentMessages.Amount(request.AmountMinor);
        var installment = Math.Max(1, request.Installments);

        // İmzaya giren alanlar tutarı ve taksiti KAPSAR: kapsamasaydı müşteri 1 ₺'lik
        // işlemi 1000 ₺ gibi gönderebilir ya da taksiti değiştirebilirdi.
        var signature = CCPaymentMessages.Sign(
            string.Join('|', amount, installment, CurrencyCode(request.Currency), merchantKey, request.OrderId),
            appSecret);

        var body = new Dictionary<string, object?>
        {
            ["cc_holder_name"] = request.Card.HolderName ?? "POYRA",
            ["cc_no"] = request.Card.Pan,
            ["expiry_month"] = request.Card.ExpiryMonth.ToString("D2"),
            ["expiry_year"] = request.Card.ExpiryYear.ToString("D4"),
            ["cvv"] = request.Card.Cvv,
            ["currency_code"] = CurrencyCode(request.Currency),
            ["installments_number"] = installment,
            ["invoice_id"] = request.OrderId,
            ["invoice_description"] = request.Description ?? request.OrderId,
            ["total"] = amount,
            ["merchant_key"] = merchantKey,
            ["items"] = new[]
            {
                new { name = request.Description ?? "Sipariş", price = amount, quantity = 1, description = "" },
            },
            ["name"] = "Poyra",
            ["surname"] = "Musteri",
            ["hash_key"] = signature,
            ["ip"] = request.CustomerIp ?? "0.0.0.0",
            ["transaction_type"] = "Auth",
            ["response_method"] = "POST",
            // Sonucu SUNUCU tamamlar: tarayıcının "tamamlandı" demesine güvenilmez.
            ["payment_completed_by"] = "merchant",
            ["return_url"] = callbackUrl,
            ["cancel_url"] = callbackUrl,
        };

        var response = await SendAsync(credentials, "api/paySmart3D", body, ct);
        var form = CCPaymentMessages.ExtractForm(response);

        if (form is not { } extracted)
            throw new ConnectorUnavailableException(
                "CCPayment 3D adımı için beklenen yönlendirme formu dönmedi.");

        return new HostedPaymentForm(extracted.ActionUrl, extracted.Fields);
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

        if (!IsSignatureValid(form, credentials, orderId))
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

        if (!IsSignatureValid(form, credentials, orderId))
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

        var body = new Dictionary<string, object?>
        {
            ["merchant_key"] = merchantKey,
            ["invoice_id"] = orderId,
            ["order_id"] = providerOrderId,
            ["status"] = "complete",
            ["app_lang"] = "tr",
            ["hash_key"] = CCPaymentMessages.Sign(
                string.Join('|', merchantKey, orderId, providerOrderId, "complete"), appSecret),
        };

        var response = await SendAsync(credentials, "payment/complete", body, ct);
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        var status = Text(root, "status_code");

        if (status != "100")
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                CCPaymentMessages.UnifiedError(status, mdStatus), status, Text(root, "status_description"));

        var data = root.TryGetProperty("data", out var d) ? d : default;
        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Text(data, "auth_code"),
            ConnectorTxnId: providerOrderId,
            MaskedPan: Text(data, "cc_no"),
            CardBank: Text(data, "card_bank"),
            UnifiedErrors.None, status, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => RefundCoreAsync(reference.OrderId, "0", credentials, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => RefundCoreAsync(request.OrderId, CCPaymentMessages.Amount(request.AmountMinor), credentials, ct);


    private async Task<ConnectorOperationResult> RefundCoreAsync(
        string orderId, string amount, ConnectorCredentials credentials, CancellationToken ct)
    {
        var merchantKey = credentials.Require("merchant_key");

        var body = new Dictionary<string, object?>
        {
            ["invoice_id"] = orderId,
            ["amount"] = amount,
            ["app_id"] = credentials.Require("app_id"),
            ["app_secret"] = credentials.Require("app_secret"),
            ["merchant_key"] = merchantKey,
            ["hash_key"] = CCPaymentMessages.Sign(
                string.Join('|', amount, orderId, merchantKey), credentials.Require("app_secret")),
        };

        var response = await SendAsync(credentials, "api/refund", body, ct);
        using var document = JsonDocument.Parse(response);
        var status = Text(document.RootElement, "status_code");

        return status == "100"
            ? ConnectorOperationResult.Ok(orderId)
            : ConnectorOperationResult.Fail(
                CCPaymentMessages.UnifiedError(status, null), status,
                Text(document.RootElement, "status_description"));
    }

    private async Task<string> GetTokenAsync(ConnectorCredentials credentials, CancellationToken ct)
    {
        var response = await SendAsync(credentials, "api/token", new Dictionary<string, object?>
        {
            ["app_id"] = credentials.Require("app_id"),
            ["app_secret"] = credentials.Require("app_secret"),
        }, ct, token: null);

        using var document = JsonDocument.Parse(response);
        if (Text(document.RootElement, "status_code") != "100")
            throw new ConnectorUnavailableException("CCPayment belirteci alınamadı.");

        var data = document.RootElement.TryGetProperty("data", out var d) ? d : default;
        return Text(data, "token")
               ?? throw new ConnectorUnavailableException("CCPayment belirteç yanıtında token yok.");
    }

    private async Task<string> SendAsync(
        ConnectorCredentials credentials, string path, Dictionary<string, object?> body,
        CancellationToken ct, string? token = "")
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = $"{credentials.Require("gateway_base").TrimEnd('/')}/{path}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };

        // belirteç == "" → "gerekiyorsa al"; null → belirteç ucunun kendisi (sonsuz döngü olmasın)
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", token.Length == 0 ? await GetTokenAsync(credentials, ct) : token);

        try
        {
            using var response = await client.SendAsync(request, ct);
            var bodyText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"CCPayment {path} → {(int)response.StatusCode}.");

            return bodyText;
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"CCPayment {path} ucuna ulaşılamadı.", ex);
        }
    }

    private static bool IsSignatureValid(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, string orderId)
    {
        var decrypted = CCPaymentMessages.Decrypt(
            form.GetValueOrDefault("hash_key"), credentials.Require("app_secret"));

        // İmza çözülüyorsa anahtar bizimkiyle aynı demektir; ilk alanın sipariş numaramızı
        // tutması da başka bir işlemin imzasının buraya taşınmadığını gösterir.
        return decrypted is not null
               && decrypted.Split('|') is [var first, ..]
               && string.Equals(first, orderId, StringComparison.Ordinal);
    }

    private static string CurrencyCode(string currency) => currency.ToUpperInvariant();

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null,
            }
            : null;
}
