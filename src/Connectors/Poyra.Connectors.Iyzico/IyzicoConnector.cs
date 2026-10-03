using System.Text;
using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Iyzico;

/// <summary>
/// <b>İyzico</b> ödeme kuruluşu — kendi API'si (IYZWSv2 imzalı REST).
///
/// <b>PCI kapsamı:</b> kart İŞYERİ tarafında toplanır ve sunucudan sunucuya iletilir;
/// banka-hosted kart girişi yoktur. Poyra'da bu 3DS'li direct akıştır, hosted akışta
/// bu hesap aday listesinden düşer.
///
/// <b>Tahsilat tarayıcı dönüşüyle kesinleşmez:</b> 3D dönüşünden sonra
/// <c>/payment/3dsecure/auth</c> çağrısı yapılır ve sonuç oradan okunur.
///
/// <b>⚠ SERTİFİKASYON DURUMU:</b> imza algoritması ve uçlar sağlayıcının genel API
/// dokümanına göre yazıldı; alan adları ve hata kodları canlı hesapla doğrulanmadan
/// üretime alınmamalı.
/// </summary>
public sealed class IyzicoConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "iyzico";
    public const string HttpClientName = "poyra-iyzico";

    private const string InitPath = "/payment/3dsecure/initialize";
    private const string CompletePath = "/payment/3dsecure/auth";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "İyzico — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "API adresi (ör. https://api.iyzipay.com)"),
            new CredentialField("api_key", "API anahtarı (apiKey)"),
            new CredentialField("secret_key", "Gizli anahtar (secretKey)", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Kart İŞYERİ formunda toplanır → PCI kapsamı. Banka-hosted giriş yoktur; "
               + "hosted akışta bu hesap atlanır. Tahsilat /payment/3dsecure/auth ile "
               + "kesinleşir. TODO(cert).");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => throw new ConnectorConfigurationException(
            "İyzico banka-hosted kart girişini desteklemiyor; 3DS'li direct akış kullanın.");

    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var amount = IyzicoMessages.Price(request.AmountMinor);
        var buyer = new
        {
            id = request.OrderId,
            name = "Poyra",
            surname = "Musteri",
            identityNumber = "11111111111", // İyzico zorunlu tutar; gerçek TCKN taşımayız
            email = "musteri@poyra.local",
            registrationAddress = "Bilinmiyor",
            city = "Istanbul",
            country = "Turkey",
            ip = request.CustomerIp ?? "0.0.0.0",
        };
        var url = new { contactName = "Poyra Musteri", city = "Istanbul", country = "Turkey", address = "Bilinmiyor" };

        var body = new
        {
            locale = "tr",
            conversationId = request.OrderId,
            price = amount,
            paidPrice = amount,
            currency = request.Currency.ToUpperInvariant(),
            installment = Math.Max(1, request.Installments),
            basketId = request.OrderId,
            paymentChannel = "WEB",
            paymentGroup = "PRODUCT",
            callbackUrl,
            paymentCard = new
            {
                cardHolderName = request.Card.HolderName ?? "POYRA MUSTERI",
                cardNumber = request.Card.Pan,
                expireYear = request.Card.ExpiryYear.ToString("D4"),
                expireMonth = request.Card.ExpiryMonth.ToString("D2"),
                cvc = request.Card.Cvv,
                registerCard = 0,
            },
            buyer = buyer,
            shippingAddress = url,
            billingAddress = url,
            basketItems = new[]
            {
                new
                {
                    id = request.OrderId,
                    name = request.Description ?? "Siparis",
                    category1 = "Genel",
                    itemType = "VIRTUAL",
                    price = amount,
                },
            },
        };

        using var response = await SendAsync(credentials, InitPath, body, ct);
        var root = response.RootElement;

        if (Text(root, "status") != "success")
            throw new ConnectorUnavailableException(
                $"İyzico 3D başlatma reddetti: {Text(root, "errorCode")} {Text(root, "errorMessage")}");

        var form = IyzicoMessages.DecodeForm(Text(root, "threeDSHtmlContent"));
        if (form is not { } extracted)
            throw new ConnectorUnavailableException("İyzico 3D yanıtında beklenen form yok.");

        return new HostedPaymentForm(extracted.ActionUrl, extracted.Fields);
    }

    /// <summary>
    /// Tarayıcı dönüşü TEK BAŞINA tahsilat kanıtı değildir — sonuç
    /// <see cref="CompleteHostedCallbackAsync"/> içindeki sunucu çağrısından okunur.
    /// Burası bu yüzden asla başarı döndürmez.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var orderId = form.GetValueOrDefault("conversationId", string.Empty);
        var mdStatus = form.GetValueOrDefault("mdStatus");

        return new HostedCallbackResult(
            false, orderId, null, null, null, null,
            mdStatus == "1"
                ? UnifiedErrors.ProcessingError // 3D geçti ama tahsilat teyidi yapılmadı
                : IyzicoMessages.UnifiedError(null, mdStatus),
            mdStatus,
            "İyzico dönüşü /payment/3dsecure/auth ile kesinleştirilmelidir.");
    }

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var orderId = form.GetValueOrDefault("conversationId", string.Empty);
        var mdStatus = form.GetValueOrDefault("mdStatus");
        var paymentId = form.GetValueOrDefault("paymentId");

        if (mdStatus != "1" || string.IsNullOrWhiteSpace(paymentId))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                IyzicoMessages.UnifiedError(null, mdStatus), mdStatus,
                "3D kimlik doğrulaması başarısız.");

        using var response = await SendAsync(credentials, CompletePath, new
        {
            locale = "tr",
            conversationId = orderId,
            paymentId,
            conversationData = form.GetValueOrDefault("conversationData"),
        }, ct);

        var root = response.RootElement;
        if (Text(root, "status") != "success")
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                IyzicoMessages.UnifiedError(Text(root, "errorCode"), mdStatus),
                Text(root, "errorCode"), Text(root, "errorMessage"));

        var item = root.TryGetProperty("itemTransactions", out var items)
                    && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0
            ? items[0]
            : default;

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Text(root, "authCode") ?? paymentId,
            // İade/iptal paymentTransactionId ile yapılır — referansı burada saklıyoruz
            ConnectorTxnId: Text(item, "paymentTransactionId") ?? paymentId,
            MaskedPan: Text(root, "binNumber") is { } bin ? bin + "******" : null,
            CardBank: Text(root, "cardAssociation"),
            UnifiedErrors.None, Text(root, "status"), null);
    }

    public async Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
    {
        using var response = await SendAsync(credentials, "/payment/cancel", new
        {
            locale = "tr",
            conversationId = reference.OrderId,
            paymentId = reference.ConnectorTxnId,
        }, ct);

        return ToOperationResult(response.RootElement, reference.ConnectorTxnId);
    }

    public async Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        using var response = await SendAsync(credentials, "/payment/refund", new
        {
            locale = "tr",
            conversationId = request.OrderId,
            paymentTransactionId = request.ConnectorTxnId,
            price = IyzicoMessages.Price(request.AmountMinor),
            currency = request.Currency.ToUpperInvariant(),
        }, ct);

        return ToOperationResult(response.RootElement, request.ConnectorTxnId);
    }

    // ---- İç yardımcılar --------------------------------------------------------

    private static ConnectorOperationResult ToOperationResult(JsonElement root, string? txnId)
        => Text(root, "status") == "success"
            ? ConnectorOperationResult.Ok(txnId)
            : ConnectorOperationResult.Fail(
                IyzicoMessages.UnifiedError(Text(root, "errorCode"), null),
                Text(root, "errorCode"), Text(root, "errorMessage"));

    private async Task<JsonDocument> SendAsync(
        ConnectorCredentials credentials, string path, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        var random = IyzicoMessages.RandomKey();

        using var request = new HttpRequestMessage(
            HttpMethod.Post, credentials.Require("gateway_base").TrimEnd('/') + path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        // İmza YOLU da kapsar: aynı gövdeyi başka bir uca göndermek doğrulamayı kırar.
        request.Headers.TryAddWithoutValidation("Authorization", IyzicoMessages.AuthorizationHeader(
            credentials.Require("api_key"), credentials.Require("secret_key"), path, json, random));
        request.Headers.TryAddWithoutValidation("x-iyzi-rnd", random);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"İyzico {path} → {(int)response.StatusCode}.");

            return JsonDocument.Parse(text);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"İyzico {path} ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("İyzico yanıtı JSON değil.", ex);
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
