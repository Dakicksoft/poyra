using System.Text;
using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Tami;

/// <summary>
/// <b>Tami</b> ödeme kuruluşu.
///
/// <b>PCI kapsamı:</b> kart İŞYERİ tarafında toplanır ve sunucudan sunucuya iletilir;
/// banka-hosted giriş yoktur. Hosted akışta bu hesap aday listesinden düşer.
///
/// <b>Dönüş doğrulaması:</b> callback bir <c>hashedData</c> taşıyor ama formülü
/// sağlayıcı dokümanında açıklanmıyor. Tahmin etmek yerine — Moka'daki gibi —
/// tahsilat <c>/payment/query</c> ile SUNUCUDAN okunuyor; tarayıcının söylediği hiçbir
/// alan kanıt sayılmıyor. Formül belgelendiğinde ek bir ön filtre olarak eklenebilir.
///
/// <b>⚠ SERTİFİKASYON DURUMU:</b> alan adları ve durum kodları canlı hesapla
/// doğrulanmadan üretime alınmamalı.
/// </summary>
public sealed class TamiConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "tami";
    public const string HttpClientName = "poyra-tami";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Tami — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "Servis adresi (ör. https://paymentapi.tami.com.tr)"),
            new CredentialField("merchant_number", "Üye işyeri no"),
            new CredentialField("terminal_number", "Terminal no"),
            new CredentialField("jwk_kid", "JWK anahtar kimliği (kid)"),
            new CredentialField("jwk_key", "JWK gizli anahtarı (k, base64url)", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Kart İŞYERİ formunda toplanır → PCI kapsamı; banka-hosted giriş yoktur. "
               + "İmza JWS/HS512 ile gövdenin TAMAMINI kapsar. Tahsilat /payment/query "
               + "ile sunucudan doğrulanır. TODO(cert).");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => throw new ConnectorConfigurationException(
            "Tami banka-hosted kart girişini desteklemiyor; 3DS'li direct akış kullanın.");

    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["orderId"] = request.OrderId,
            ["amount"] = TamiMessages.Amount(request.AmountMinor),
            ["currency"] = request.Currency.ToUpperInvariant(),
            ["installmentCount"] = Math.Max(1, request.Installments),
            ["paymentGroup"] = "PRODUCT",
            ["paymentChannel"] = "WEB",
            ["callbackUrl"] = callbackUrl,
            ["card"] = new
            {
                number = request.Card.Pan,
                holderName = request.Card.HolderName ?? "POYRA MUSTERI",
                cvv = request.Card.Cvv,
                expireMonth = request.Card.ExpiryMonth.ToString("D2"),
                expireYear = request.Card.ExpiryYear.ToString("D4"),
            },
            ["buyer"] = new
            {
                ipAddress = request.CustomerIp ?? "0.0.0.0",
                buyerId = request.OrderId,
                name = "Poyra",
                surName = "Musteri",
                emailAddress = "musteri@poyra.local",
            },
        };

        using var response = await SendAsync(credentials, "payment/auth", body, ct);
        var root = response.RootElement;

        var encoded = Text(root, "threeDSHtmlContent");
        if (string.IsNullOrWhiteSpace(encoded))
            throw new ConnectorUnavailableException(
                $"Tami 3D içeriği dönmedi: {Text(root, "errorCode")} {Text(root, "errorMessage")}");

        (string, Dictionary<string, string>)? form;
        try
        {
            form = ConnectorHtml.ExtractForm(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }
        catch (FormatException ex)
        {
            throw new ConnectorUnavailableException("Tami 3D içeriği base64 değil.", ex);
        }

        if (form is not { } extracted)
            throw new ConnectorUnavailableException("Tami 3D içeriğinde beklenen form yok.");

        return new HostedPaymentForm(extracted.Item1, extracted.Item2);
    }

    /// <summary>
    /// Callback'teki <c>hashedData</c>'nın formülü belgelenmediği için tarayıcı dönüşü
    /// tek başına kanıt sayılmaz; sonuç <see cref="CompleteHostedCallbackAsync"/>
    /// içindeki sunucu sorgusundan okunur.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, form.GetValueOrDefault("orderId", string.Empty), null, null, null, null,
            TamiMessages.UnifiedError(null, form.GetValueOrDefault("mdStatus")),
            form.GetValueOrDefault("mdStatus"),
            "Tami dönüşü /payment/query ile kesinleştirilmelidir.");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var orderId = form.GetValueOrDefault("orderId", string.Empty);
        var mdStatus = form.GetValueOrDefault("mdStatus");

        if (mdStatus != "1")
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                TamiMessages.UnifiedError(null, mdStatus), mdStatus,
                "3D kimlik doğrulaması başarısız.");

        using var query = await SendAsync(credentials, "payment/query", new Dictionary<string, object?>
        {
            ["orderId"] = orderId,
            ["isTransactionDetail"] = "true",
        }, ct);

        var root = query.RootElement;
        var status = Text(root, "paymentStatus");

        // İki koşul birden: işlem başarılı VE sipariş yetkilendirilmiş olmalı.
        var succeeded = root.TryGetProperty("success", out var flag)
                       && flag.ValueKind == JsonValueKind.True
                       && status == "SUCCESS";

        if (!succeeded)
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                TamiMessages.UnifiedError(status, mdStatus), status, Text(root, "errorMessage"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Text(root, "authCode"),
            ConnectorTxnId: Text(root, "transactionId") ?? orderId,
            MaskedPan: form.GetValueOrDefault("maskedNumber"),
            CardBank: form.GetValueOrDefault("cardBrand"),
            UnifiedErrors.None, status, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, "payment/reverse", new Dictionary<string, object?>
        {
            ["orderId"] = reference.OrderId,
        }, reference.ConnectorTxnId, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, "payment/refund", new Dictionary<string, object?>
        {
            ["orderId"] = request.OrderId,
            ["amount"] = TamiMessages.Amount(request.AmountMinor),
        }, request.ConnectorTxnId, ct);


    private async Task<ConnectorOperationResult> OperationAsync(
        ConnectorCredentials credentials, string path, Dictionary<string, object?> body,
        string? txnId, CancellationToken ct)
    {
        using var response = await SendAsync(credentials, path, body, ct);
        var succeeded = response.RootElement.TryGetProperty("success", out var flag)
                       && flag.ValueKind == JsonValueKind.True;

        return succeeded
            ? ConnectorOperationResult.Ok(txnId)
            : ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError,
                Text(response.RootElement, "errorCode"),
                Text(response.RootElement, "errorMessage"));
    }

    private async Task<JsonDocument> SendAsync(
        ConnectorCredentials credentials, string path, Dictionary<string, object?> body,
        CancellationToken ct)
    {
        // İmza gövdenin TAMAMINI kapsar ve securityHash'in kendisi hesaba katılmaz —
        // bu yüzden önce imzasız gövde serileştirilir, sonra alan eklenir.
        var unsigned = JsonSerializer.Serialize(body);
        var signature = TamiMessages.SecurityHash(
            unsigned, credentials.Require("jwk_kid"), credentials.Require("jwk_key"));

        body["securityHash"] = signature;
        var json = JsonSerializer.Serialize(body);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{credentials.Require("gateway_base").TrimEnd('/')}/{path}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("PG-Api-Version", "v3");
        request.Headers.TryAddWithoutValidation("PG-Auth-Token",
            $"{credentials.Require("merchant_number")}:{credentials.Require("terminal_number")}:{signature}");
        request.Headers.TryAddWithoutValidation("correlationId", Guid.NewGuid().ToString("N"));

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"Tami {path} → {(int)response.StatusCode}.");

            return JsonDocument.Parse(text);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"Tami {path} ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("Tami yanıtı JSON değil.", ex);
        }
    }

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
