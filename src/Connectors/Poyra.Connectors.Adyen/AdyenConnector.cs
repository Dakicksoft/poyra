using System.Net.Http.Json;
using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Adyen;

public sealed class AdyenConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "adyen";
    public const string HttpClientName = "poyra-adyen";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Adyen",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base",
                "Checkout adresi (test: https://checkout-test.adyen.com, canlı: hesabınıza özel)"),
            new CredentialField("api_key", "API anahtarı", Secret: true),
            new CredentialField("merchant_account", "Üye işyeri hesabı (merchantAccount)"),
        ],
        SupportsInstallments: false,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Yurt dışı satış içindir. Pay by Link ile barındırılan ödeme sayfası; "
               + "3DS/SCA Adyen tarafında. Türkiye banka taksidi YOKTUR. "
               + "Canlıda gateway adresi hesaba özeldir (live prefix).");

    public async Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["merchantAccount"] = credentials.Require("merchant_account"),
            ["reference"] = request.OrderId,
            ["amount"] = new Dictionary<string, object>
            {
                ["currency"] = request.Currency.ToUpperInvariant(),
                ["value"] = AdyenAmount.ToApi(request.AmountMinor, request.Currency),
            },
            ["returnUrl"] = request.CallbackUrl,
            ["description"] = Truncate(request.Description ?? "Ödeme", 80),
            // Bağlantı tek işlem içindir; süresi dolduğunda ödenemez
            ["reusable"] = false,
        };

        var link = await SendAsync(credentials, HttpMethod.Post, "v71/paymentLinks",
            payload, request.OrderId, ct);

        var url = Text(link, "url")
            ?? throw new ConnectorConfigurationException("Adyen ödeme bağlantısı adres döndürmedi.");

        // Bağlantı kimliği dönüşte GEREKLİDİR (sonucu onunla sorarız) ama Adyen onu
        // dönüş adresine eklemez. Tarayıcıya emanet etmek de olmaz — kurcalanabilir ve
        // başka bir işlemin sonucu okutulabilirdi. Bu yüzden KONNEKTÖR DURUMU olarak
        // saklanır; callback'te bize geri verilir.
        var linkId = Text(link, "id")
            ?? throw new ConnectorConfigurationException("Adyen ödeme bağlantısı kimlik döndürmedi.");

        return new HostedPaymentForm(
            url, new Dictionary<string, string>(), Method: "GET",
            ConnectorState: new Dictionary<string, string> { ["poyra_link_id"] = linkId });
    }

    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, Get(form, "poyra_link_id") ?? "", null, null, null, "Adyen",
            UnifiedErrors.ProcessingError, null,
            "Adyen dönüşü sunucu doğrulaması gerektirir (CompleteHostedCallbackAsync).");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var linkId = Get(form, "poyra_link_id");
        if (string.IsNullOrEmpty(linkId))
            return new HostedCallbackResult(false, "", null, null, null, "Adyen",
                UnifiedErrors.ProcessingError, null, "Adyen bağlantı kimliği dönüşte yok.");

        var link = await SendAsync(credentials, HttpMethod.Get, $"v71/paymentLinks/{linkId}",
            null, null, ct);

        var orderId = Text(link, "reference") ?? "";
        var status = Text(link, "status");

        // completed = ödendi. Diğerleri (active/expired/paymentPending) tahsilat DEĞİLDİR;
        // "active" müşterinin sayfayı kapatıp geri geldiği durumdur.
        if (status != "completed")
        {
            return new HostedCallbackResult(false, orderId, null, null, null, "Adyen",
                status == "expired" ? UnifiedErrors.ThreeDsTimeout : UnifiedErrors.ThreeDsFailed,
                status, "Adyen ödemesi tamamlanmadı.");
        }

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: null,
            // pspReference iade/iptalin anahtarıdır — yoksa para geri verilemez
            ConnectorTxnId: Text(link, "pspReference"),
            MaskedPan: null,
            CardBank: "Adyen",
            UnifiedCode: "",
            RawCode: status,
            RawMessage: null);
    }

    public async Task<DirectAuthorizeResult?> AuthorizeDirectAsync(
        DirectPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["merchantAccount"] = credentials.Require("merchant_account"),
            ["reference"] = request.OrderId,
            ["amount"] = new Dictionary<string, object>
            {
                ["currency"] = request.Currency.ToUpperInvariant(),
                ["value"] = AdyenAmount.ToApi(request.AmountMinor, request.Currency),
            },
            ["paymentMethod"] = new Dictionary<string, object?>
            {
                ["type"] = "scheme",
                ["number"] = request.Card.Pan,
                ["expiryMonth"] = request.Card.ExpiryMonth.ToString("00"),
                ["expiryYear"] = request.Card.ExpiryYear.ToString(),
                ["cvc"] = request.Card.Cvv,
                ["holderName"] = request.Card.HolderName,
            },
        };

        JsonElement payment;
        try
        {
            payment = await SendAsync(credentials, HttpMethod.Post, "v71/payments",
                payload, request.OrderId, ct);
        }
        catch (AdyenApiException ex)
        {
            return new DirectAuthorizeResult(false, null, null, CardNumbers.Mask(request.Card.Pan),
                AdyenErrorMap.ToUnified(ex.ErrorCode), ex.ErrorCode, ex.Message);
        }

        var resultCode = Text(payment, "resultCode");

        return resultCode == "Authorised"
            ? new DirectAuthorizeResult(true, null, Text(payment, "pspReference"),
                CardNumbers.Mask(request.Card.Pan), "", resultCode, null)
            : new DirectAuthorizeResult(false, null, Text(payment, "pspReference"),
                CardNumbers.Mask(request.Card.Pan),
                AdyenErrorMap.FromResultCode(resultCode, Text(payment, "refusalReasonCode")),
                resultCode, Text(payment, "refusalReason"));
    }

    public async Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
    {
        if (reference.ConnectorTxnId is not { Length: > 0 } psp)
            return ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError, null, "Adyen iptali için pspReference gerekli.");

        try
        {
            var result = await SendAsync(credentials, HttpMethod.Post,
                $"v71/payments/{psp}/cancels",
                new Dictionary<string, object?>
                {
                    ["merchantAccount"] = credentials.Require("merchant_account"),
                    ["reference"] = reference.OrderId,
                },
                $"{reference.OrderId}-cancel", ct);

            return ConnectorOperationResult.Ok(Text(result, "pspReference"));
        }
        catch (AdyenApiException ex)
        {
            return ConnectorOperationResult.Fail(
                AdyenErrorMap.ToUnified(ex.ErrorCode), ex.ErrorCode, ex.Message);
        }
    }

    public async Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        if (request.ConnectorTxnId is not { Length: > 0 } psp)
            return ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError, null, "Adyen iadesi için pspReference gerekli.");

        try
        {
            var result = await SendAsync(credentials, HttpMethod.Post,
                $"v71/payments/{psp}/refunds",
                new Dictionary<string, object?>
                {
                    ["merchantAccount"] = credentials.Require("merchant_account"),
                    ["reference"] = request.OrderId,
                    ["amount"] = new Dictionary<string, object>
                    {
                        ["currency"] = request.Currency.ToUpperInvariant(),
                        ["value"] = AdyenAmount.ToApi(request.AmountMinor, request.Currency),
                    },
                },
                // Kısmi iadeler ayrışsın: aynı ödemeye iki farklı iade çakışmamalı
                $"{request.OrderId}-refund-{request.AmountMinor}", ct);

            return ConnectorOperationResult.Ok(Text(result, "pspReference"));
        }
        catch (AdyenApiException ex)
        {
            return ConnectorOperationResult.Fail(
                AdyenErrorMap.ToUnified(ex.ErrorCode), ex.ErrorCode, ex.Message);
        }
    }

    public async Task<ConnectorProbeResult?> ProbeAsync(
        ConnectorCredentials credentials, CancellationToken ct)
    {
        try
        {
            // Ödeme yöntemi listesi: hesabı ve anahtarı doğrular, para hareketi yaratmaz
            await SendAsync(credentials, HttpMethod.Post, "v71/paymentMethods",
                new Dictionary<string, object?>
                {
                    ["merchantAccount"] = credentials.Require("merchant_account"),
                }, null, ct);

            return new ConnectorProbeResult(true, "Adyen erişilebilir.");
        }
        catch (AdyenApiException ex)
        {
            return new ConnectorProbeResult(false, $"Adyen: {ex.ErrorCode} {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ConnectorProbeResult(false, ex.Message);
        }
    }

    private async Task<JsonElement> SendAsync(
        ConnectorCredentials credentials, HttpMethod method, string path,
        object? payload, string? idempotencyKey, CancellationToken ct)
    {
        var gatewayBase = credentials.Require("gateway_base").TrimEnd('/');
        using var request = new HttpRequestMessage(method, $"{gatewayBase}/{path}");

        request.Headers.Add("X-API-Key", credentials.Require("api_key"));
        if (idempotencyKey is { Length: > 0 })
            request.Headers.Add("Idempotency-Key", idempotencyKey);

        if (payload is not null)
            request.Content = JsonContent.Create(payload);

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorUnavailableException($"Adyen'e ulaşılamadı: {ex.Message}", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException($"Adyen yanıtı ayrıştırılamadı: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement.Clone();

            if (response.IsSuccessStatusCode)
                return root;

            // 5xx = sağlayıcı sorunu → failover'a değer; 4xx = iş hatası → failover anlamsız
            if ((int)response.StatusCode >= 500)
                throw new ConnectorUnavailableException($"Adyen HTTP {(int)response.StatusCode}");

            throw new AdyenApiException(
                Text(root, "errorCode") ?? $"http_{(int)response.StatusCode}",
                Text(root, "message") ?? "Adyen hatası");
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

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}

public sealed class AdyenApiException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Adyen tutarı "minor units"te ister; sıfır ondalıklı (JPY) ve ÜÇ ondalıklı
/// (KWD, BHD, JOD…) para birimleri farklıdır. Poyra her zaman KURUŞ (iki ondalık)
/// taşır — çevrilmezse Kuveyt'e 10 kat eksik, Japonya'ya 100 kat fazla fatura kesilir.
/// </summary>
public static class AdyenAmount
{
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "CVE", "DJF", "GNF", "IDR", "JPY", "KMF", "KRW", "PYG",
        "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    private static readonly HashSet<string> ThreeDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND",
    };

    public static long ToApi(long amountMinor, string currency)
    {
        if (ZeroDecimal.Contains(currency))
            return amountMinor / 100;
        if (ThreeDecimal.Contains(currency))
            return amountMinor * 10;

        return amountMinor;
    }

    public static long FromApi(long apiAmount, string currency)
    {
        if (ZeroDecimal.Contains(currency))
            return apiAmount * 100;
        if (ThreeDecimal.Contains(currency))
            return apiAmount / 10;

        return apiAmount;
    }
}

public static class AdyenErrorMap
{
    public static string ToUnified(string? errorCode) => errorCode switch
    {
        null or "" => UnifiedErrors.ProcessingError,
        "14_4" or "14_5" or "14_6" => UnifiedErrors.InvalidCard,     // kart alanı doğrulaması
        "101" => UnifiedErrors.InvalidCard,                          // geçersiz kart numarası
        "103" => UnifiedErrors.InvalidCard,                          // CVC uzunluğu
        "104" or "105" => UnifiedErrors.NotPermitted,
        "010" or "901" or "905" => UnifiedErrors.ConnectorUnavailable, // hesap/yapılandırma
        _ => UnifiedErrors.ProcessingError,
    };

    public static string FromResultCode(string? resultCode, string? refusalReasonCode)
        => refusalReasonCode switch
        {
            "2" => UnifiedErrors.CardDeclined,           // Refused
            "3" => UnifiedErrors.ProcessingError,        // Referral
            "4" => UnifiedErrors.IssuerUnavailable,      // Acquirer Error
            "5" => UnifiedErrors.CardDeclined,           // Blocked Card — ayrıntı verilmez
            "6" => UnifiedErrors.ExpiredCard,
            "7" => UnifiedErrors.ProcessingError,        // Invalid Amount
            "8" => UnifiedErrors.InvalidCard,            // Invalid Card Number
            "9" => UnifiedErrors.IssuerUnavailable,
            "10" => UnifiedErrors.IssuerUnavailable,
            "11" => UnifiedErrors.ThreeDsFailed,         // 3D Not Authenticated
            "12" => UnifiedErrors.InsufficientFunds,
            "14" => UnifiedErrors.CardDeclined,          // Acquirer Fraud — ayrıntı verilmez
            "18" => UnifiedErrors.LimitExceeded,         // Restricted Card
            "22" => UnifiedErrors.CardDeclined,          // Fraud
            "24" => UnifiedErrors.InvalidCard,           // CVC Declined
            "26" => UnifiedErrors.NotPermitted,          // Revocation of Auth
            _ => resultCode switch
            {
                "Refused" => UnifiedErrors.CardDeclined,
                "Cancelled" => UnifiedErrors.NotPermitted,
                "Error" => UnifiedErrors.ProcessingError,
                "RedirectShopper" or "ChallengeShopper" => UnifiedErrors.ThreeDsFailed,
                _ => UnifiedErrors.ProcessingError,
            },
        };
}
