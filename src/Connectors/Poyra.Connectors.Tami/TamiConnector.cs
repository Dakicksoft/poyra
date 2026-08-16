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
        var govde = new Dictionary<string, object?>
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

        using var yanit = await GonderAsync(credentials, "payment/auth", govde, ct);
        var kok = yanit.RootElement;

        var kodlu = Metin(kok, "threeDSHtmlContent");
        if (string.IsNullOrWhiteSpace(kodlu))
            throw new ConnectorUnavailableException(
                $"Tami 3D içeriği dönmedi: {Metin(kok, "errorCode")} {Metin(kok, "errorMessage")}");

        (string, Dictionary<string, string>)? form;
        try
        {
            form = ConnectorHtml.FormuCikar(Encoding.UTF8.GetString(Convert.FromBase64String(kodlu)));
        }
        catch (FormatException ex)
        {
            throw new ConnectorUnavailableException("Tami 3D içeriği base64 değil.", ex);
        }

        if (form is not { } cikan)
            throw new ConnectorUnavailableException("Tami 3D içeriğinde beklenen form yok.");

        return new HostedPaymentForm(cikan.Item1, cikan.Item2);
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

        using var sorgu = await GonderAsync(credentials, "payment/query", new Dictionary<string, object?>
        {
            ["orderId"] = orderId,
            ["isTransactionDetail"] = "true",
        }, ct);

        var kok = sorgu.RootElement;
        var durum = Metin(kok, "paymentStatus");

        // İki koşul birden: işlem başarılı VE sipariş yetkilendirilmiş olmalı.
        var basarili = kok.TryGetProperty("success", out var bayrak)
                       && bayrak.ValueKind == JsonValueKind.True
                       && durum == "SUCCESS";

        if (!basarili)
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                TamiMessages.UnifiedError(durum, mdStatus), durum, Metin(kok, "errorMessage"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Metin(kok, "authCode"),
            ConnectorTxnId: Metin(kok, "transactionId") ?? orderId,
            MaskedPan: form.GetValueOrDefault("maskedNumber"),
            CardBank: form.GetValueOrDefault("cardBrand"),
            UnifiedErrors.None, durum, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, "payment/reverse", new Dictionary<string, object?>
        {
            ["orderId"] = reference.OrderId,
        }, reference.ConnectorTxnId, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, "payment/refund", new Dictionary<string, object?>
        {
            ["orderId"] = request.OrderId,
            ["amount"] = TamiMessages.Amount(request.AmountMinor),
        }, request.ConnectorTxnId, ct);


    private async Task<ConnectorOperationResult> IslemAsync(
        ConnectorCredentials credentials, string yol, Dictionary<string, object?> govde,
        string? txnId, CancellationToken ct)
    {
        using var yanit = await GonderAsync(credentials, yol, govde, ct);
        var basarili = yanit.RootElement.TryGetProperty("success", out var bayrak)
                       && bayrak.ValueKind == JsonValueKind.True;

        return basarili
            ? ConnectorOperationResult.Ok(txnId)
            : ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError,
                Metin(yanit.RootElement, "errorCode"),
                Metin(yanit.RootElement, "errorMessage"));
    }

    private async Task<JsonDocument> GonderAsync(
        ConnectorCredentials credentials, string yol, Dictionary<string, object?> govde,
        CancellationToken ct)
    {
        // İmza gövdenin TAMAMINI kapsar ve securityHash'in kendisi hesaba katılmaz —
        // bu yüzden önce imzasız gövde serileştirilir, sonra alan eklenir.
        var imzasiz = JsonSerializer.Serialize(govde);
        var imza = TamiMessages.SecurityHash(
            imzasiz, credentials.Require("jwk_kid"), credentials.Require("jwk_key"));

        govde["securityHash"] = imza;
        var json = JsonSerializer.Serialize(govde);

        using var istek = new HttpRequestMessage(
            HttpMethod.Post, $"{credentials.Require("gateway_base").TrimEnd('/')}/{yol}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        istek.Headers.TryAddWithoutValidation("PG-Api-Version", "v3");
        istek.Headers.TryAddWithoutValidation("PG-Auth-Token",
            $"{credentials.Require("merchant_number")}:{credentials.Require("terminal_number")}:{imza}");
        istek.Headers.TryAddWithoutValidation("correlationId", Guid.NewGuid().ToString("N"));

        try
        {
            var istemci = httpClientFactory.CreateClient(HttpClientName);
            using var yanit = await istemci.SendAsync(istek, ct);
            var metin = await yanit.Content.ReadAsStringAsync(ct);

            if (!yanit.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"Tami {yol} → {(int)yanit.StatusCode}.");

            return JsonDocument.Parse(metin);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"Tami {yol} ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("Tami yanıtı JSON değil.", ex);
        }
    }

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
