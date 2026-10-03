using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Lidio;

/// <summary>
/// <b>Lidio</b> (eski adıyla Mobilexpress) ödeme kuruluşu — JSON REST.
///
/// İki akışı da destekler:
/// <list type="bullet">
/// <item>Hosted ödeme sayfası (<c>StartHostedPaymentProcess</c>) — kart Lidio'da girilir,
/// <b>PCI kapsamı dışı</b>.</item>
/// <item>3DS'li direct (<c>ProcessPayment</c> + <c>FinishPaymentProcess</c>) — kart bizim
/// formumuzda toplanır, <b>PCI kapsamı içi</b>.</item>
/// </list>
///
/// <b>Dönüş doğrulaması:</b> Lidio tarayıcıyı sonuç parametreleriyle dönüş adresine
/// yönlendirir; bu parametreler kanıt DEĞİLDİR. Hosted akışta sonuç
/// <c>GetHostedPaymentStatus</c> sorgusundan okunur; direct akışta 3D dönüşü yalnız
/// doğrulamayı bitirir — para <c>FinishPaymentProcess</c> çağrılmadan bankadan
/// çekilmez, sonucu da o çağrının yanıtı belirler.
///
/// Sorgu kimlikleri (sipariş no, <c>systemTransId</c>) ve tutar başlatma anında
/// KONNEKTÖR DURUMU olarak saklanır. Durum anahtarları <c>poyra_</c> önekli: callback
/// birleştirmesinde tarayıcının alanları kazanır ve Lidio dönüşte <c>OrderId</c> /
/// <c>SystemTransId</c> adlarını kullanır — önek olmasa kendi dönüş parametreleri durumu
/// ezerdi. Önekli anahtarı bilerek enjekte eden biri ise yalnız sorulan siparişi
/// değiştirebilir; sonuç o sipariş numarasıyla döner ve callback işleyicisi deneme
/// kimliğiyle eşleşmeyen siparişi reddeder.
///
/// <b>⚠ SERTİFİKASYON DURUMU / TODO(cert):</b> uçlar, alan adları ve sonuç kodları
/// sağlayıcının yayımlanmış API belgesine göre yazıldı; canlı hesapla doğrulanmadı.
/// Açık noktalar: hesabın müşteri tekil alanı (e-posta mı müşteri no mu — Poyra müşteri
/// no olarak sipariş numarasını gönderiyor), servis adresinin <c>/api</c> öneki.
/// </summary>
public sealed class LidioConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "lidio";
    public const string HttpClientName = "poyra-lidio";

    private const string StartHostedPath = "/StartHostedPaymentProcess";
    private const string HostedStatusPath = "/GetHostedPaymentStatus";
    private const string ProcessPaymentPath = "/ProcessPayment";
    private const string FinishPaymentPath = "/FinishPaymentProcess";
    private const string CancelPath = "/Cancel";
    private const string RefundPath = "/Refund";
    private const string InquiryPath = "/PaymentInquiry";

    // Poyra yalnız yeni kartla ödeme açar; kayıtlı kart Lidio'nun değil Poyra kasasının işi.
    private const string CardInstrument = "NewCard";

    // Tarayıcının dönüşte gönderdiği OrderId/SystemTransId bunların üzerine yazamasın diye önekli.
    private const string StateOrderId = "poyra_lidio_order_id";
    private const string StateSystemTransId = "poyra_lidio_system_trans_id";
    private const string StateAmount = "poyra_lidio_amount";
    private const string StateCurrency = "poyra_lidio_currency";
    private const string StateFlow = "poyra_lidio_flow";
    private const string FlowHosted = "hosted";
    private const string FlowDirect = "direct";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Lidio — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "API adresi (test: https://test.lidio.com/api · canlı: https://api.lidio.com)"),
            new CredentialField("merchant_code", "İşyeri kodu (MerchantCode)"),
            new CredentialField("api_key", "API anahtarı (MxS2S)", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Hosted ödeme sayfası (PCI kapsamı dışı) ve 3DS'li direct (PCI kapsamı içi) "
               + "birlikte desteklenir. Dönüş parametreleri kanıt sayılmaz: hosted sonuç "
               + "GetHostedPaymentStatus ile sorgulanır, direct'te para FinishPaymentProcess "
               + "ile çekilir. Canlıda API çağrısı yapılan IP'ler Lidio'ya tanımlatılmalıdır "
               + "(aksi hâlde InvalidCredential). TODO(cert).");


    public async Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var amount = LidioMessages.Amount(request.AmountMinor);
        var currency = LidioMessages.Currency(request.Currency);
        var installments = Math.Max(1, request.Installments);

        using var response = await SendAsync(credentials, StartHostedPath, new
        {
            orderId = request.OrderId,
            merchantProcessId = request.OrderId,
            totalAmount = amount,
            currency,
            customerInfo = new { customerId = request.OrderId },
            paymentInstruments = new[] { CardInstrument },
            paymentInstrumentInfo = new
            {
                card = new
                {
                    processType = "sales",
                    // Taksit seçeneği müşteriye AÇILMAZ — sayı customParameters ile sabitlenir.
                    useInstallment = false,
                    useLoyaltyPoints = false,
                    newCard = new { threeDSecureMode = "Mandatory", useIVRForCardEntry = false },
                },
            },
            customParameters = LidioMessages.CustomParameters(installments),
            returnUrl = request.CallbackUrl,
            clientType = "Web",
            clientIp = request.CustomerIp,
        }, ct);

        var root = response.RootElement;
        var redirectUrl = Text(root, "redirectURL");

        if (!LidioMessages.IsApproved(Text(root, "result")) || string.IsNullOrWhiteSpace(redirectUrl))
            throw new ConnectorUnavailableException(
                $"Lidio ödeme sayfası açılamadı: {Text(root, "result")} {Text(root, "resultMessage")}");

        // Lidio form değil hazır ADRES döner — GET yönlendirmesi (alan yok).
        return new HostedPaymentForm(
            redirectUrl, new Dictionary<string, string>(), Method: "GET",
            ConnectorState: BuildState(FlowHosted, request.OrderId, Text(root, "systemTransId"),
                request.AmountMinor, currency));
    }


    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var currency = LidioMessages.Currency(request.Currency);

        using var response = await SendAsync(credentials, ProcessPaymentPath, new
        {
            orderId = request.OrderId,
            merchantProcessId = request.OrderId,
            totalAmount = LidioMessages.Amount(request.AmountMinor),
            currency,
            customerInfo = new { customerId = request.OrderId },
            paymentInstrument = CardInstrument,
            paymentInstrumentInfo = new
            {
                newCard = new
                {
                    processType = "sales",
                    cardInfo = new
                    {
                        cardHolderName = request.Card.HolderName ?? "POYRA MUSTERI",
                        cardNumber = request.Card.Pan,
                        lastMonth = request.Card.ExpiryMonth,
                        lastYear = request.Card.ExpiryYear,
                    },
                    cvv = request.Card.Cvv,
                    use3DSecure = true,
                    installmentCount = Math.Max(1, request.Installments),
                    saveCardTemporarily = false,
                    saveAfterSuccess = false,
                },
            },
            returnUrl = callbackUrl,
            clientType = "Web",
            clientIp = request.CustomerIp,
        }, ct);

        var root = response.RootElement;

        if (Text(root, "result") != "RedirectFormCreated"
            || ConnectorHtml.FormuCikar(Text(root, "redirectForm") ?? string.Empty) is not { } form)
            throw new ConnectorUnavailableException(
                $"Lidio 3D formu dönmedi: {Text(root, "result")} {Text(root, "resultDetail")} "
                + Text(root, "resultMessage"));

        var systemTransId = Text(Child(root, "paymentInfo"), "systemTransId") ?? Text(root, "systemTransId");

        return new HostedPaymentForm(
            form.ActionUrl, form.Fields,
            ConnectorState: BuildState(FlowDirect, request.OrderId, systemTransId, request.AmountMinor, currency));
    }

    /// <summary>
    /// Dönüş parametreleri imzasız sayılır (3D dönüşündeki hash, hesabın müşteri tekil
    /// alanına bağlı ve tek başına parayı kanıtlamaz). Sonuç her iki akışta da
    /// <see cref="CompleteHostedCallbackAsync"/> içindeki sunucu çağrısından okunur;
    /// burası bu yüzden asla başarı döndürmez.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, Field(form, StateOrderId) ?? Field(form, "OrderId") ?? string.Empty,
            null, null, null, null,
            UnifiedErrors.ProcessingError, Field(form, "Result"),
            "Lidio dönüşü sunucu çağrısıyla kesinleştirilmelidir (CompleteHostedCallbackAsync).");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        // Sipariş no YALNIZ durumdan okunur. Durum yoksa hangi ödemeyi sorduğumuzu
        // bilemeyiz: tarayıcının söylediğine dönmek yerine başarısız saymak, yanlış bir
        // "ödendi"den her hâlükârda ucuzdur.
        var orderId = Field(form, StateOrderId);
        if (string.IsNullOrEmpty(orderId))
            return Failed(Field(form, "OrderId") ?? string.Empty,
                "Lidio sorgu kimliği dönüşte yok; sonuç doğrulanamadı.");

        // systemTransId başlatma yanıtında yoksa dönüşteki değer kullanılır. Bu güvenlidir:
        // sipariş no durumdan geliyor ve Lidio ikisini birlikte eşler — başka bir siparişin
        // işlem numarası bu siparişle sorgulanınca bulunamaz.
        var systemTransId = Field(form, StateSystemTransId) ?? Field(form, "SystemTransId");
        if (string.IsNullOrEmpty(systemTransId))
            return Failed(orderId, "Lidio işlem numarası (systemTransId) yok; sonuç doğrulanamadı.");

        var amountMinor = long.TryParse(Field(form, StateAmount), CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (long?)null;

        return Field(form, StateFlow) == FlowDirect
            ? await FinishDirectAsync(credentials, orderId, systemTransId, amountMinor, Field(form, StateCurrency), ct)
            : await QueryHostedAsync(credentials, orderId, systemTransId, amountMinor, ct);
    }

    private async Task<HostedCallbackResult> QueryHostedAsync(
        ConnectorCredentials credentials, string orderId, string systemTransId, long? amountMinor,
        CancellationToken ct)
    {
        using var response = await SendAsync(credentials, HostedStatusPath, new
        {
            systemTransId,
            orderId,
            clientType = "Web",
        }, ct);

        var root = response.RootElement;

        // Hosted sayfada müşteri reddedilen karttan sonra başka kartla yeniden deneyebilir;
        // liste bu yüzden birden çok deneme taşıyabilir. Aranan, başarılı ve sonradan
        // otomatik iptal EDİLMEMİŞ olanı.
        var payments = Child(root, "paymentList");
        var succeeded = Items(payments).FirstOrDefault(p =>
            Flag(p, "isSuccess") == true && Flag(p, "isCancelled") != true);

        if (LidioMessages.IsApproved(Text(root, "result"))
            && LidioMessages.IsApproved(Text(root, "paymentResult"))
            && succeeded.ValueKind == JsonValueKind.Object)
            return Approved(orderId, succeeded, amountMinor);

        var last = Items(payments).LastOrDefault();
        return Declined(orderId, last, Text(root, "paymentResult") ?? Text(root, "result"),
            Text(root, "resultMessage"));
    }

    private async Task<HostedCallbackResult> FinishDirectAsync(
        ConnectorCredentials credentials, string orderId, string systemTransId, long? amountMinor,
        string? currency, CancellationToken ct)
    {
        // 3D dönüşünün "3DSuccess"/"3DFailed" iddiasına bakılmaz: başarısız doğrulamayı
        // Lidio bu çağrıda zaten ThreeDValidationFailed ile reddeder.
        using var response = await SendAsync(credentials, FinishPaymentPath, new
        {
            orderId,
            systemTransId,
            totalAmount = amountMinor is { } minor ? LidioMessages.Amount(minor) : (decimal?)null,
            currency,
            paymentInstrument = CardInstrument,
            paymentInstrumentInfo = new { newCard = new { } },
            clientType = "Web",
        }, ct);

        var root = response.RootElement;
        var payment = Child(root, "paymentInfo");

        return LidioMessages.IsApproved(Text(root, "result"))
            ? Approved(orderId, payment, amountMinor)
            : Declined(orderId, payment, Text(root, "resultDetail") ?? Text(root, "result"),
                Text(root, "resultMessage"));
    }

    private static HostedCallbackResult Approved(string orderId, JsonElement payment, long? amountMinor)
    {
        // Sağlayıcı belgesi açıkça ister: dönen tutar sepet tutarıyla karşılaştırılmalı.
        // Tutmayan bir "başarı" ya kurcalanmış bir oturumdur ya da bizim hatamız; ikisinde
        // de ödendi demek yanlış olur — fark mutabakatta yakalanır.
        var requested = ReadDecimal(payment, "amountRequested");
        if (amountMinor is { } minor && requested is { } lidioAmount && lidioAmount != LidioMessages.Amount(minor))
            return Failed(orderId,
                $"Lidio tutarı ({lidioAmount.ToString(CultureInfo.InvariantCulture)}) sipariş tutarıyla eşleşmiyor.");

        var returnedOrderId = Text(payment, "orderId");
        if (returnedOrderId is not null && returnedOrderId != orderId)
            return Failed(orderId, "Lidio yanıtındaki sipariş numarası eşleşmiyor.");

        var card = Child(Child(payment, "instrumentDetail"), "card");
        var pos = Child(Child(payment, "acquirerResultDetail"), "pos");

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Text(pos, "authCode"),
            ConnectorTxnId: Text(payment, "systemTransId"),
            MaskedPan: Text(card, "maskedCardNumber"),
            CardBank: Text(card, "cardBankName"),
            UnifiedErrors.None, Text(Child(payment, "resultCategory"), "categoryCode"), null);
    }

    private static HostedCallbackResult Declined(
        string orderId, JsonElement payment, string? resultCode, string? resultMessage)
    {
        var category = Text(Child(payment, "resultCategory"), "categoryCode");
        var pos = Child(Child(payment, "acquirerResultDetail"), "pos");

        return new HostedCallbackResult(
            false, orderId, null, null, null, null,
            LidioMessages.UnifiedError(category, resultCode),
            // Ham kod önceliği: bankanın kendi kodu → Lidio kategorisi → Lidio sonucu.
            Text(pos, "returnCode") ?? category ?? resultCode,
            Text(pos, "message") ?? resultMessage);
    }


    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, CancelPath, new
        {
            orderId = reference.OrderId,
            paymentInstrument = CardInstrument,
            clientType = "Web",
        }, reference.ConnectorTxnId, LidioMessages.IsApproved, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, RefundPath, new
        {
            orderId = request.OrderId,
            totalAmount = LidioMessages.Amount(request.AmountMinor),
            currency = LidioMessages.Currency(request.Currency),
            paymentInstrument = CardInstrument,
            clientType = "Web",
        }, request.ConnectorTxnId, LidioMessages.IsRefundApproved, ct);

    public async Task<ConnectorProbeResult?> ProbeAsync(ConnectorCredentials credentials, CancellationToken ct)
    {
        try
        {
            // Var olmayan siparişi sorgulamak para hareketi yaratmaz; yanıtın "bulunamadı"
            // olması anahtarın, işyeri kodunun ve IP tanımının geçerli olduğunu gösterir.
            using var response = await SendAsync(credentials, InquiryPath, new
            {
                orderId = "poyra-canary-000",
                paymentInstrument = CardInstrument,
                paymentInquiryInstrumentInfo = new { card = new { processType = "sales" } },
                clientType = "Web",
            }, ct);

            var result = Text(response.RootElement, "result");
            return result == "InvalidCredential"
                ? new ConnectorProbeResult(false, "Lidio kimlik bilgisi ya da IP tanımı geçersiz.")
                : new ConnectorProbeResult(true, $"Lidio erişilebilir ({result}).");
        }
        catch (Exception ex) when (ex is ConnectorUnavailableException or ConnectorConfigurationException)
        {
            return new ConnectorProbeResult(false, ex.Message);
        }
    }

    // ---- İç yardımcılar --------------------------------------------------------

    private static Dictionary<string, string> BuildState(
        string flow, string orderId, string? systemTransId, long amountMinor, string currency)
    {
        var state = new Dictionary<string, string>
        {
            [StateFlow] = flow,
            [StateOrderId] = orderId,
            [StateAmount] = amountMinor.ToString(CultureInfo.InvariantCulture),
            [StateCurrency] = currency,
        };
        if (!string.IsNullOrEmpty(systemTransId))
            state[StateSystemTransId] = systemTransId;
        return state;
    }

    private async Task<ConnectorOperationResult> OperationAsync(
        ConnectorCredentials credentials, string path, object body, string? txnId,
        Func<string?, bool> isApproved, CancellationToken ct)
    {
        using var response = await SendAsync(credentials, path, body, ct);
        var root = response.RootElement;
        var result = Text(root, "result");

        if (isApproved(result))
            return ConnectorOperationResult.Ok(txnId);

        var detail = Text(root, "resultDetail") ?? result;
        return ConnectorOperationResult.Fail(
            LidioMessages.UnifiedError(null, detail), detail,
            Text(root, "resultMessage") ?? $"Lidio {path.TrimStart('/')} onaylanmadı ({result}).");
    }

    private async Task<JsonDocument> SendAsync(
        ConnectorCredentials credentials, string path, object body, CancellationToken ct)
    {
        // Kimlik alanları HTTP'den ÖNCE okunur: eksik alan ağ hatası gibi görünmesin,
        // işyerine "POS bilgilerinizi tamamlayın" olarak dönsün.
        var url = credentials.Require("gateway_base").TrimEnd('/') + path;
        var authorization = LidioMessages.Authorization(credentials.Require("api_key"));
        var merchantCode = credentials.Require("merchant_code");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("MerchantCode", merchantCode);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            // 4xx gövdesi sonuç kodunu taşır ve çağıran onu birleşik koda çevirebilmeli;
            // yalnız 5xx/ağ hatası "konnektör ayakta değil" sayılır.
            if ((int)response.StatusCode >= 500)
                throw new ConnectorUnavailableException($"Lidio {path} → {(int)response.StatusCode}.");

            return JsonDocument.Parse(content);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"Lidio {path} ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("Lidio yanıtı JSON değil.", ex);
        }
    }

    private static HostedCallbackResult Failed(string orderId, string message)
        => new(false, orderId, null, null, null, null, UnifiedErrors.ProcessingError, null, message);

    /// <summary>Dönüş sorgu dizesinden gelir; anahtarların büyük/küçük harfine güvenilmez.</summary>
    private static string? Field(IReadOnlyDictionary<string, string> form, string name)
        => form.TryGetValue(name, out var value)
            ? value
            : form.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static JsonElement Child(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child)
            ? child
            : default;

    private static IEnumerable<JsonElement> Items(JsonElement element)
        => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];

    private static bool? Flag(JsonElement element, string name)
        => Child(element, name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    private static decimal? ReadDecimal(JsonElement element, string name)
        => Child(element, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetDecimal(out var d)
            ? d
            : null;

    private static string? Text(JsonElement element, string name)
        => Child(element, name) switch
        {
            { ValueKind: JsonValueKind.String } text => text.GetString(),
            { ValueKind: JsonValueKind.Number } number => number.ToString(),
            _ => null,
        };
}
