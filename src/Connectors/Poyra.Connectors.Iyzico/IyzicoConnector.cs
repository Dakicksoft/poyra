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

    private const string BaslatYolu = "/payment/3dsecure/initialize";
    private const string TamamlaYolu = "/payment/3dsecure/auth";

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
        var tutar = IyzicoMessages.Price(request.AmountMinor);
        var alici = new
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
        var adres = new { contactName = "Poyra Musteri", city = "Istanbul", country = "Turkey", address = "Bilinmiyor" };

        var govde = new
        {
            locale = "tr",
            conversationId = request.OrderId,
            price = tutar,
            paidPrice = tutar,
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
            buyer = alici,
            shippingAddress = adres,
            billingAddress = adres,
            basketItems = new[]
            {
                new
                {
                    id = request.OrderId,
                    name = request.Description ?? "Siparis",
                    category1 = "Genel",
                    itemType = "VIRTUAL",
                    price = tutar,
                },
            },
        };

        using var yanit = await GonderAsync(credentials, BaslatYolu, govde, ct);
        var kok = yanit.RootElement;

        if (Metin(kok, "status") != "success")
            throw new ConnectorUnavailableException(
                $"İyzico 3D başlatma reddetti: {Metin(kok, "errorCode")} {Metin(kok, "errorMessage")}");

        var form = IyzicoMessages.FormuCoz(Metin(kok, "threeDSHtmlContent"));
        if (form is not { } cikan)
            throw new ConnectorUnavailableException("İyzico 3D yanıtında beklenen form yok.");

        return new HostedPaymentForm(cikan.ActionUrl, cikan.Fields);
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

        using var yanit = await GonderAsync(credentials, TamamlaYolu, new
        {
            locale = "tr",
            conversationId = orderId,
            paymentId,
            conversationData = form.GetValueOrDefault("conversationData"),
        }, ct);

        var kok = yanit.RootElement;
        if (Metin(kok, "status") != "success")
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                IyzicoMessages.UnifiedError(Metin(kok, "errorCode"), mdStatus),
                Metin(kok, "errorCode"), Metin(kok, "errorMessage"));

        var kalem = kok.TryGetProperty("itemTransactions", out var kalemler)
                    && kalemler.ValueKind == JsonValueKind.Array && kalemler.GetArrayLength() > 0
            ? kalemler[0]
            : default;

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Metin(kok, "authCode") ?? paymentId,
            // İade/iptal paymentTransactionId ile yapılır — referansı burada saklıyoruz
            ConnectorTxnId: Metin(kalem, "paymentTransactionId") ?? paymentId,
            MaskedPan: Metin(kok, "binNumber") is { } bin ? bin + "******" : null,
            CardBank: Metin(kok, "cardAssociation"),
            UnifiedErrors.None, Metin(kok, "status"), null);
    }

    public async Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
    {
        using var yanit = await GonderAsync(credentials, "/payment/cancel", new
        {
            locale = "tr",
            conversationId = reference.OrderId,
            paymentId = reference.ConnectorTxnId,
        }, ct);

        return SonucaCevir(yanit.RootElement, reference.ConnectorTxnId);
    }

    public async Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        using var yanit = await GonderAsync(credentials, "/payment/refund", new
        {
            locale = "tr",
            conversationId = request.OrderId,
            paymentTransactionId = request.ConnectorTxnId,
            price = IyzicoMessages.Price(request.AmountMinor),
            currency = request.Currency.ToUpperInvariant(),
        }, ct);

        return SonucaCevir(yanit.RootElement, request.ConnectorTxnId);
    }

    // ---- İç yardımcılar --------------------------------------------------------

    private static ConnectorOperationResult SonucaCevir(JsonElement kok, string? txnId)
        => Metin(kok, "status") == "success"
            ? ConnectorOperationResult.Ok(txnId)
            : ConnectorOperationResult.Fail(
                IyzicoMessages.UnifiedError(Metin(kok, "errorCode"), null),
                Metin(kok, "errorCode"), Metin(kok, "errorMessage"));

    private async Task<JsonDocument> GonderAsync(
        ConnectorCredentials credentials, string yol, object govde, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(govde);
        var rastgele = IyzicoMessages.RastgeleAnahtar();

        using var istek = new HttpRequestMessage(
            HttpMethod.Post, credentials.Require("gateway_base").TrimEnd('/') + yol)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        // İmza YOLU da kapsar: aynı gövdeyi başka bir uca göndermek doğrulamayı kırar.
        istek.Headers.TryAddWithoutValidation("Authorization", IyzicoMessages.YetkiBasligi(
            credentials.Require("api_key"), credentials.Require("secret_key"), yol, json, rastgele));
        istek.Headers.TryAddWithoutValidation("x-iyzi-rnd", rastgele);

        try
        {
            var istemci = httpClientFactory.CreateClient(HttpClientName);
            using var yanit = await istemci.SendAsync(istek, ct);
            var metin = await yanit.Content.ReadAsStringAsync(ct);

            if (!yanit.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"İyzico {yol} → {(int)yanit.StatusCode}.");

            return JsonDocument.Parse(metin);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"İyzico {yol} ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("İyzico yanıtı JSON değil.", ex);
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
