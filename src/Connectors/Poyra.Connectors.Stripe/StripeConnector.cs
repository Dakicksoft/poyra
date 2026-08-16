using System.Net.Http.Headers;
using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Stripe;

public sealed class StripeConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "stripe";
    public const string HttpClientName = "poyra-stripe";
    private const string ApiBase = "https://api.stripe.com/v1";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Stripe (e-ihracat)",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("secret_key", "Gizli anahtar (sk_live_… / sk_test_…)", Secret: true),
            new CredentialField("statement_descriptor", "Ekstrede görünecek ad", Required: false),
        ],
        SupportsInstallments: false,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Yurt dışı satış içindir. Checkout Session ile barındırılan ödeme sayfası; "
               + "3DS/SCA Stripe tarafında. Türkiye banka taksidi YOKTUR.");

    public async Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("client_reference_id", request.OrderId),
            // Dönüş adresine oturum kimliği eklenir; sonucu YİNE DE sunucudan sorarız
            new("success_url", AppendSession(request.CallbackUrl)),
            new("cancel_url", AppendSession(request.CallbackUrl)),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", request.Currency.ToLowerInvariant()),
            new("line_items[0][price_data][unit_amount]",
                StripeAmount.ToApi(request.AmountMinor, request.Currency).ToString()),
            new("line_items[0][price_data][product_data][name]",
                Truncate(request.Description ?? "Ödeme", 250)),
            // Sipariş numarası ödemeye de yazılır: Stripe panelinde ve webhook'ta görünür
            new("payment_intent_data[metadata][poyra_order]", request.OrderId),
        };

        if (credentials.Get("statement_descriptor") is { Length: > 0 } descriptor)
            form.Add(new KeyValuePair<string, string>(
                "payment_intent_data[statement_descriptor]", Truncate(descriptor, 22)));

        var session = await PostAsync(credentials, "checkout/sessions", form, request.OrderId, ct);

        var url = session.GetProperty("url").GetString()
            ?? throw new ConnectorConfigurationException("Stripe oturumu adres döndürmedi.");

        // GET yönlendirmesi: adres sorgu dizesi taşır, form POST'una çevrilemez
        return new HostedPaymentForm(url, new Dictionary<string, string>(), Method: "GET");
    }

    /// <summary>
    /// Stripe dönüşü yalnız "müşteri geri geldi" demektir; ödeme olup olmadığını
    /// SÖYLEMEZ. Bu yüzden biçimsel doğrulama burada yapılmaz, karar sunucu
    /// sorgusundadır (bkz. CompleteHostedCallbackAsync).
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, Get(form, "session_id") ?? "", null, null, null, "Stripe",
            UnifiedErrors.ProcessingError, null,
            "Stripe dönüşü sunucu doğrulaması gerektirir (CompleteHostedCallbackAsync).");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var sessionId = Get(form, "session_id");
        if (string.IsNullOrEmpty(sessionId))
            return new HostedCallbackResult(false, "", null, null, null, "Stripe",
                UnifiedErrors.ProcessingError, null, "Stripe oturum kimliği dönüşte yok.");

        var session = await GetAsync(credentials, $"checkout/sessions/{sessionId}", ct);
        var orderId = session.TryGetProperty("client_reference_id", out var reference)
            ? reference.GetString() ?? ""
            : "";

        var paymentStatus = session.TryGetProperty("payment_status", out var status)
            ? status.GetString()
            : null;

        if (paymentStatus != "paid")
        {
            // Müşteri vazgeçti ya da ödeme tamamlanmadı — banka reddi değildir
            var unified = paymentStatus == "unpaid"
                ? UnifiedErrors.ThreeDsFailed
                : UnifiedErrors.ProcessingError;
            return new HostedCallbackResult(false, orderId, null, null, null, "Stripe",
                unified, paymentStatus, "Stripe ödemesi tamamlanmadı.");
        }

        // payment_intent alanı genişletilmediyse yalnız kimlik gelir; iade/iptal için yeterli
        var paymentIntentId = session.TryGetProperty("payment_intent", out var intent)
            ? intent.ValueKind == JsonValueKind.String ? intent.GetString() : intent.GetProperty("id").GetString()
            : null;

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: null, // Stripe yetkilendirme kodu vermez; referans payment_intent'tir
            ConnectorTxnId: paymentIntentId,
            MaskedPan: null,
            CardBank: "Stripe",
            UnifiedCode: "",
            RawCode: paymentStatus,
            RawMessage: null);
    }

    /// <summary>
    /// Ham kart verisiyle ödeme. Stripe bunu YALNIZ PCI onaylı hesaplara açar
    /// (raw card data permission); onaysız hesapta API hata döner. Onaysızken
    /// hosted akış kullanılmalıdır.
    /// </summary>
    public async Task<DirectAuthorizeResult?> AuthorizeDirectAsync(
        DirectPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("amount", StripeAmount.ToApi(request.AmountMinor, request.Currency).ToString()),
            new("currency", request.Currency.ToLowerInvariant()),
            new("confirm", "true"),
            new("payment_method_data[type]", "card"),
            new("payment_method_data[card][number]", request.Card.Pan),
            new("payment_method_data[card][exp_month]", request.Card.ExpiryMonth.ToString()),
            new("payment_method_data[card][exp_year]", request.Card.ExpiryYear.ToString()),
            new("metadata[poyra_order]", request.OrderId),
            // 3DS gerekirse otomatik yönlendirme İSTEMEYİZ: bu uç 3DS'siz satış içindir,
            // aksi halde müşteri hiçbir yere gitmeden ödeme askıda kalırdı
            new("automatic_payment_methods[enabled]", "true"),
            new("automatic_payment_methods[allow_redirects]", "never"),
        };

        if (request.Card.Cvv is { Length: > 0 } cvv)
            form.Add(new KeyValuePair<string, string>("payment_method_data[card][cvc]", cvv));

        JsonElement intent;
        try
        {
            intent = await PostAsync(credentials, "payment_intents", form, request.OrderId, ct);
        }
        catch (StripeApiException ex)
        {
            return new DirectAuthorizeResult(false, null, null, CardNumbers.Mask(request.Card.Pan),
                StripeErrorMap.ToUnified(ex.DeclineCode ?? ex.Code), ex.Code, ex.Message);
        }

        var status = intent.TryGetProperty("status", out var value) ? value.GetString() : null;

        return status == "succeeded"
            ? new DirectAuthorizeResult(true, null, intent.GetProperty("id").GetString(),
                CardNumbers.Mask(request.Card.Pan), "", status, null)
            : new DirectAuthorizeResult(false, null, intent.GetProperty("id").GetString(),
                CardNumbers.Mask(request.Card.Pan),
                UnifiedErrors.CardDeclined, status, $"Stripe durumu: {status}");
    }

    public async Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
    {
        if (reference.ConnectorTxnId is not { Length: > 0 } paymentIntentId)
            return ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError, null, "Stripe iptali için payment_intent gerekli.");

        try
        {
            var result = await PostAsync(credentials, $"payment_intents/{paymentIntentId}/cancel",
                [], reference.OrderId, ct);
            return ConnectorOperationResult.Ok(result.GetProperty("id").GetString());
        }
        catch (StripeApiException ex)
        {
            // Yakalanmış (captured) ödeme iptal edilemez — Stripe'da bu iade işidir
            return ConnectorOperationResult.Fail(
                StripeErrorMap.ToUnified(ex.Code), ex.Code, ex.Message);
        }
    }

    public async Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        if (request.ConnectorTxnId is not { Length: > 0 } paymentIntentId)
            return ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError, null, "Stripe iadesi için payment_intent gerekli.");

        var form = new List<KeyValuePair<string, string>>
        {
            new("payment_intent", paymentIntentId),
            new("amount", StripeAmount.ToApi(request.AmountMinor, request.Currency).ToString()),
        };

        try
        {
            var refund = await PostAsync(credentials, "refunds", form,
                $"{request.OrderId}-refund-{request.AmountMinor}", ct);
            return ConnectorOperationResult.Ok(refund.GetProperty("id").GetString());
        }
        catch (StripeApiException ex)
        {
            return ConnectorOperationResult.Fail(
                StripeErrorMap.ToUnified(ex.Code), ex.Code, ex.Message);
        }
    }

    public async Task<ConnectorProbeResult?> ProbeAsync(
        ConnectorCredentials credentials, CancellationToken ct)
    {
        try
        {
            // Hesabın kendisini sorar: anahtar geçersizse 401 döner, işlem denemeye gerek yok
            await GetAsync(credentials, "balance", ct);
            return new ConnectorProbeResult(true, "Stripe erişilebilir.");
        }
        catch (StripeApiException ex)
        {
            return new ConnectorProbeResult(false, $"Stripe: {ex.Code} {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ConnectorProbeResult(false, ex.Message);
        }
    }

    private async Task<JsonElement> PostAsync(
        ConnectorCredentials credentials, string path,
        IReadOnlyCollection<KeyValuePair<string, string>> form, string idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/{path}");
        request.Content = new FormUrlEncodedContent(form);

        // Idempotency-Key: ağ koptuğunda yeniden denemek ÇİFT ÇEKİM yapmasın.
        // Anahtar sipariş numarasıdır — aynı deneme aynı anahtarla gider.
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await SendAsync(credentials, request, ct);
    }

    private async Task<JsonElement> GetAsync(
        ConnectorCredentials credentials, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/{path}");
        return await SendAsync(credentials, request, ct);
    }

    private async Task<JsonElement> SendAsync(
        ConnectorCredentials credentials, HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", credentials.Require("secret_key"));

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorUnavailableException($"Stripe'a ulaşılamadı: {ex.Message}", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException($"Stripe yanıtı ayrıştırılamadı: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement.Clone();

            if (response.IsSuccessStatusCode)
                return root;

            // 5xx = sağlayıcı sorunu → failover'a değer; 4xx = iş hatası → failover anlamsız
            if ((int)response.StatusCode >= 500)
                throw new ConnectorUnavailableException($"Stripe HTTP {(int)response.StatusCode}");

            var error = root.TryGetProperty("error", out var e) ? e : default;
            throw new StripeApiException(
                Code: Text(error, "code") ?? Text(error, "type") ?? $"http_{(int)response.StatusCode}",
                DeclineCode: Text(error, "decline_code"),
                Message: Text(error, "message") ?? "Stripe hatası");
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Get(IReadOnlyDictionary<string, string> form, string key)
        => form.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Dönüş adresine Stripe'ın oturum kimliği yer tutucusunu ekler.</summary>
    private static string AppendSession(string callbackUrl)
        => callbackUrl + (callbackUrl.Contains('?') ? "&" : "?") + "session_id={CHECKOUT_SESSION_ID}";

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}

public sealed class StripeApiException(string Code, string? DeclineCode, string Message)
    : Exception(Message)
{
    public string Code { get; } = Code;
    public string? DeclineCode { get; } = DeclineCode;
}

/// <summary>
/// Stripe tutarı "en küçük birim"de ister ama SIFIR ONDALIKLI para birimlerinde
/// (JPY, KRW…) o birim liranın kendisidir. Poyra her zaman kuruş taşır; burada
/// çevrilmezse Japonya'ya 100 kat fazla fatura kesilir.
/// </summary>
public static class StripeAmount
{
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA",
        "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    public static long ToApi(long amountMinor, string currency)
        => ZeroDecimal.Contains(currency) ? amountMinor / 100 : amountMinor;

    public static long FromApi(long apiAmount, string currency)
        => ZeroDecimal.Contains(currency) ? apiAmount * 100 : apiAmount;

    public static bool IsZeroDecimal(string currency) => ZeroDecimal.Contains(currency);
}

public static class StripeErrorMap
{
    public static string ToUnified(string? code) => code switch
    {
        null or "" => UnifiedErrors.ProcessingError,
        "insufficient_funds" => UnifiedErrors.InsufficientFunds,
        "expired_card" => UnifiedErrors.ExpiredCard,
        "incorrect_number" or "invalid_number" or "invalid_expiry_month"
            or "invalid_expiry_year" or "incorrect_cvc" or "invalid_cvc" => UnifiedErrors.InvalidCard,
        "card_velocity_exceeded" or "withdrawal_count_limit_exceeded" => UnifiedErrors.LimitExceeded,
        "transaction_not_allowed" or "service_not_allowed" => UnifiedErrors.NotPermitted,
        "authentication_required" => UnifiedErrors.ThreeDsFailed,
        "issuer_not_available" or "processing_error" or "try_again_later" => UnifiedErrors.IssuerUnavailable,
        // lost_card / stolen_card müşteriye AYRINTI VERMEDEN reddedilir
        _ => UnifiedErrors.CardDeclined,
    };
}
