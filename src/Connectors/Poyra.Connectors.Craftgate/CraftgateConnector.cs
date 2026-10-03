using System.Globalization;
using System.Text;
using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Craftgate;

/// <summary>
/// <b>Craftgate</b> ödeme orkestrasyonu — imzalı JSON REST.
///
/// İki akışı da destekler:
/// <list type="bullet">
/// <item>Ortak Ödeme Sayfası (hosted) — kart Craftgate'te girilir, <b>PCI kapsamı dışı</b>.</item>
/// <item>3DS'li direct — kart bizim formumuzda toplanır, <b>PCI kapsamı içi</b>.</item>
/// </list>
///
/// <b>Dönüş doğrulaması:</b> her iki akışta da tarayıcı dönüşü kanıt DEĞİLDİR ve
/// sonuç sunucudan okunur. Kritik nokta şu: dönüşü sorgulamak için gereken kimlik
/// (ortak sayfada <c>token</c>, direct'te <c>paymentId</c>) tarayıcıya emanet edilmez —
/// başlatma yanıtından alınıp KONNEKTÖR DURUMU olarak saklanır. Craftgate aynı adları
/// dönüşte de POST'lar; durum anahtarlarımız <c>poyra_</c> önekli olduğu için tarayıcının
/// gönderdiği değerler bizimkilerin üzerine yazamaz (callback birleştirmesinde forma
/// öncelik verilir). Yazabilseydi başka bir ödemenin sonucu okutulabilirdi.
///
/// <b>İptal:</b> Craftgate'te ayrı bir iptal ucu yoktur — <c>/payment/v1/refunds</c>
/// gün içi ve kısmi iadesi olmayan işlemde CANCEL, aksi hâlde REFUND üretir. Kararı
/// sağlayıcı verdiği için hangisi olduğunu ham kod alanında taşıyoruz.
///
/// <b>⚠ SERTİFİKASYON DURUMU / TODO(cert):</b> imza dizisi, uçlar ve alan adları
/// sağlayıcının genel API belgelerine ve açık istemcilerine göre yazıldı; hata grubu
/// listesi eksik. Canlı hesapla doğrulanmadan üretime alınmamalı.
/// </summary>
public sealed class CraftgateConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "craftgate";
    public const string HttpClientName = "poyra-craftgate";

    private const string CheckoutInitPath = "/payment/v1/checkout-payments/init";
    private const string ThreeDsInitPath = "/payment/v1/card-payments/3ds-init";
    private const string ThreeDsCompletePath = "/payment/v1/card-payments/3ds-complete";
    private const string RefundPath = "/payment/v1/refunds";
    private const string ItemRefundPath = "/payment/v1/refund-transactions";

    // Tarayıcının POST'ladığı "token"/"paymentId" bunların üzerine yazamasın diye önekli.
    private const string StateToken = "poyra_cg_token";
    private const string StatePaymentId = "poyra_cg_payment_id";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Craftgate — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "API adresi (ör. https://api.craftgate.io)"),
            new CredentialField("api_key", "API anahtarı (apiKey)"),
            new CredentialField("secret_key", "Gizli anahtar (secretKey)", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Ortak Ödeme Sayfası (PCI kapsamı dışı) ve 3DS'li direct (PCI kapsamı içi) "
               + "birlikte desteklenir. Sonuç her iki akışta da SUNUCUDAN sorgulanır; sorgu "
               + "kimliği konnektör durumunda saklanır, tarayıcıdan alınmaz. İptal ayrı uç "
               + "değildir: iade ucu gün içi işlemde CANCEL üretir. İmza SERVİS ADRESİNİ de "
               + "kapsar — sandbox imzası canlıda geçmez. TODO(cert).");


    public async Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var amount = CraftgateMessages.Price(request.AmountMinor);
        var installment = Math.Max(1, request.Installments);

        using var response = await SendAsync(credentials, HttpMethod.Post, CheckoutInitPath, new
        {
            price = amount,
            paidPrice = amount,
            currency = request.Currency.ToUpperInvariant(),
            paymentGroup = "PRODUCT",
            paymentPhase = "AUTH",
            paymentChannel = "WEB",
            conversationId = request.OrderId,
            externalId = request.OrderId,
            callbackUrl = request.CallbackUrl,
            clientIp = request.CustomerIp,
            // Taksit sayısı yukarıda çoktan karara bağlandı; sayfada tek seçenek açılır ki
            // müşteri başka bir taksite geçip tahsilat tutarını değiştiremesin.
            enabledInstallments = new[] { installment },
            items = new[]
            {
                new { name = request.Description ?? "Siparis", price = amount, externalId = request.OrderId },
            },
        }, ct);

        var root = response.RootElement;
        var page = Text(root, "pageUrl")
            ?? throw new ConnectorUnavailableException("Craftgate ortak ödeme sayfası adresi dönmedi.");
        var token = Text(root, "token")
            ?? throw new ConnectorUnavailableException("Craftgate ortak ödeme sayfası token dönmedi.");

        return new HostedPaymentForm(
            page, new Dictionary<string, string>(), Method: "GET",
            ConnectorState: new Dictionary<string, string> { [StateToken] = token });
    }


    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var amount = CraftgateMessages.Price(request.AmountMinor);

        using var response = await SendAsync(credentials, HttpMethod.Post, ThreeDsInitPath, new
        {
            price = amount,
            paidPrice = amount,
            currency = request.Currency.ToUpperInvariant(),
            installment = Math.Max(1, request.Installments),
            paymentGroup = "PRODUCT",
            paymentPhase = "AUTH",
            paymentChannel = "WEB",
            conversationId = request.OrderId,
            externalId = request.OrderId,
            clientIp = request.CustomerIp,
            callbackUrl,
            card = new
            {
                cardHolderName = request.Card.HolderName ?? "POYRA MUSTERI",
                cardNumber = request.Card.Pan,
                expireYear = request.Card.ExpiryYear.ToString("D4", CultureInfo.InvariantCulture),
                expireMonth = request.Card.ExpiryMonth.ToString("D2", CultureInfo.InvariantCulture),
                cvc = request.Card.Cvv,
                storeCardAfterSuccessPayment = false,
            },
            items = new[]
            {
                new { name = request.Description ?? "Siparis", price = amount, externalId = request.OrderId },
            },
        }, ct);

        var root = response.RootElement;

        var form = CraftgateMessages.DecodeForm(Text(root, "htmlContent"));
        if (form is not { } extracted)
            throw new ConnectorUnavailableException("Craftgate 3D yanıtında beklenen form yok.");

        // Tamamlama çağrısı bu kimlikle yapılır. Dönüşte tarayıcı da bir paymentId
        // POST'lar ama ona bakmıyoruz — başkasının ödemesini tamamlatabilirdi.
        var paymentId = Text(root, "paymentId")
            ?? throw new ConnectorUnavailableException("Craftgate 3D başlatma paymentId dönmedi.");

        return new HostedPaymentForm(
            extracted.ActionUrl, extracted.Fields,
            ConnectorState: new Dictionary<string, string> { [StatePaymentId] = paymentId });
    }

    /// <summary>
    /// Tarayıcı dönüşü TEK BAŞINA tahsilat kanıtı değildir — her iki akışta da sonuç
    /// <see cref="CompleteHostedCallbackAsync"/> içindeki sunucu çağrısından okunur.
    /// Burası bu yüzden asla başarı döndürmez.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, form.GetValueOrDefault("conversationId", string.Empty),
            null, null, null, null,
            UnifiedErrors.ProcessingError, null,
            "Craftgate dönüşü sunucu sorgusuyla kesinleştirilmelidir (CompleteHostedCallbackAsync).");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var token = form.GetValueOrDefault(StateToken);
        var paymentId = form.GetValueOrDefault(StatePaymentId);

        // Ortak sayfa akışı token ile, direct akış paymentId ile sorulur. İkisi de yoksa
        // sonuç bilinemez: tarayıcının gönderdiğine dönmek yerine başarısız saymak,
        // yanlış bir "ödendi"den her hâlükârda ucuzdur.
        using var response =
            !string.IsNullOrEmpty(token)
                ? await SendAsync(credentials, HttpMethod.Get,
                    $"/payment/v1/checkout-payments/{Uri.EscapeDataString(token)}", null, ct)
            : long.TryParse(paymentId, CultureInfo.InvariantCulture, out var number)
                ? await SendAsync(credentials, HttpMethod.Post, ThreeDsCompletePath,
                    new { paymentId = number }, ct)
                : null;

        return response is null
            ? Failed(form, "Craftgate sorgu kimliği dönüşte yok; sonuç doğrulanamadı.")
            : ReadResult(response.RootElement, form);
    }

    private static HostedCallbackResult ReadResult(JsonElement root, IReadOnlyDictionary<string, string> form)
    {
        var status = Text(root, "paymentStatus");
        var orderId = Text(root, "conversationId")
                      ?? form.GetValueOrDefault("conversationId", string.Empty);

        if (!CraftgateMessages.IsApproved(status))
        {
            var (errorGroup, code, message) = ReadError(root);
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                CraftgateMessages.UnifiedError(errorGroup, code), code ?? status, message);
        }

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Text(root, "authCode"),
            // İptal/iade paymentId ile yapılır — referansı burada saklıyoruz.
            ConnectorTxnId: Text(root, "id"),
            MaskedPan: Text(root, "binNumber") is { } bin ? bin + "******" : null,
            CardBank: Text(root, "cardIssuerBankName"),
            UnifiedErrors.None, status, null);
    }


    public async Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
    {
        if (!long.TryParse(reference.ConnectorTxnId, CultureInfo.InvariantCulture, out var paymentId))
            return ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError, null, "Craftgate ödeme numarası yok; iptal yapılamaz.");

        using var response = await SendAsync(credentials, HttpMethod.Post, RefundPath, new
        {
            paymentId = paymentId,
            conversationId = reference.OrderId,
            refundDestinationType = "PROVIDER",
        }, ct);

        var root = response.RootElement;
        if (!CraftgateMessages.IsRefundApproved(Text(root, "status")))
            return RefundError(root);

        // refundType = CANCEL | REFUND — hangisi olduğu ham kodda kalır.
        return new ConnectorOperationResult(
            true, reference.ConnectorTxnId, null, Text(root, "refundType"), null);
    }

    public async Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        if (!long.TryParse(request.ConnectorTxnId, CultureInfo.InvariantCulture, out var paymentId))
            return ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError, null, "Craftgate ödeme numarası yok; iade yapılamaz.");

        var itemId = await GetItemIdAsync(credentials, paymentId, ct);
        if (itemId is null)
            return ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError, null,
                "Craftgate ödeme kalemi okunamadı; iade tutarı bir kaleme bağlanamadı.");

        using var response = await SendAsync(credentials, HttpMethod.Post, ItemRefundPath, new
        {
            paymentTransactionId = itemId.Value,
            conversationId = request.OrderId,
            refundPrice = CraftgateMessages.Price(request.AmountMinor),
            refundDestinationType = "PROVIDER",
        }, ct);

        var root = response.RootElement;
        return CraftgateMessages.IsRefundApproved(Text(root, "status"))
            ? ConnectorOperationResult.Ok(request.ConnectorTxnId)
            : RefundError(root);
    }

    private async Task<long?> GetItemIdAsync(
        ConnectorCredentials credentials, long paymentId, CancellationToken ct)
    {
        using var response = await SendAsync(credentials, HttpMethod.Get,
            $"/payment/v1/card-payments/{paymentId.ToString(CultureInfo.InvariantCulture)}", null, ct);

        if (!response.RootElement.TryGetProperty("paymentTransactions", out var items)
            || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            return null;

        // Poyra tek kalemli sepet gönderiyor; birden fazlası gelirse iade tutarının
        // hangi kaleme yazılacağı belirsizdir ve sessizce ilkine yazmak yanlış olur.
        if (items.GetArrayLength() > 1) return null;

        return long.TryParse(Text(items[0], "id"), CultureInfo.InvariantCulture, out var itemId)
            ? itemId
            : null;
    }


    private static ConnectorOperationResult RefundError(JsonElement root)
    {
        var (errorGroup, code, message) = ReadError(root);
        return ConnectorOperationResult.Fail(
            CraftgateMessages.UnifiedError(errorGroup, code),
            code ?? Text(root, "status"),
            message ?? "Craftgate iade/iptal onaylanmadı.");
    }

    private static HostedCallbackResult Failed(
        IReadOnlyDictionary<string, string> form, string message)
        => new(false, form.GetValueOrDefault("conversationId", string.Empty),
            null, null, null, null, UnifiedErrors.ProcessingError, null, message);

    private static (string? Group, string? Code, string? Message) ReadError(JsonElement root)
    {
        var source = root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("paymentError", out var error)
                     && error.ValueKind == JsonValueKind.Object
            ? error
            : root;

        return (Text(source, "errorGroup"), Text(source, "errorCode"), Text(source, "errorDescription"));
    }

    private async Task<JsonDocument> SendAsync(
        ConnectorCredentials credentials, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var url = credentials.Require("gateway_base").TrimEnd('/');
        var json = body is null ? string.Empty : JsonSerializer.Serialize(body);
        var random = CraftgateMessages.RandomKey();

        using var request = new HttpRequestMessage(method, url + path);
        if (body is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        // İmza yolu ve SERVİS ADRESİNİ de kapsar: aynı gövdeyi başka bir uca ya da
        // başka bir ortama göndermek doğrulamayı kırar.
        request.Headers.TryAddWithoutValidation("x-api-key", credentials.Require("api_key"));
        request.Headers.TryAddWithoutValidation("x-rnd-key", random);
        request.Headers.TryAddWithoutValidation("x-auth-version", "v1");
        request.Headers.TryAddWithoutValidation("x-signature", CraftgateMessages.Signature(
            url, path, credentials.Require("api_key"), credentials.Require("secret_key"),
            random, json));
        request.Headers.TryAddWithoutValidation("accept", "application/json");

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            // 4xx gövdesi hata ayrıntısını taşır ve çağıran onu okuyup birleşik koda
            // çevirebilmeli; yalnız 5xx/ağ hatası "konnektör ayakta değil" sayılır.
            if ((int)response.StatusCode >= 500)
                throw new ConnectorUnavailableException($"Craftgate {path} → {(int)response.StatusCode}.");

            return JsonDocument.Parse(text);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"Craftgate {path} ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("Craftgate yanıtı JSON değil.", ex);
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
