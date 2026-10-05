using System.Globalization;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Lidio;

/// <summary>
/// <b>Lidio</b> (eski adıyla Mobilexpress) ödeme kuruluşu — JSON REST.
///
/// Üç akışı da destekler:
/// <list type="bullet">
/// <item>Hosted ödeme sayfası (<c>StartHostedPaymentProcess</c>) — kart Lidio'da girilir,
/// <b>PCI kapsamı dışı</b>.</item>
/// <item>3DS'li direct (<c>ProcessPayment</c> + <c>FinishPaymentProcess</c>) — kart bizim
/// formumuzda toplanır, <b>PCI kapsamı içi</b>.</item>
/// <item>3D'siz direct (<c>ProcessPayment</c>, <c>use3DSecure=false</c>) — kasadaki kartla
/// tekrarlayan tahsilat gibi müşterinin olmadığı çekimler.</item>
/// </list>
/// Saklı kart, ön provizyon ve taksit sorgusu gibi birleşik arayüzde karşılığı olmayan
/// metotlar <see cref="LidioClient"/> üzerindedir.
///
/// <b>Dönüş doğrulaması:</b> Lidio tarayıcıyı sonuç parametreleriyle dönüş adresine
/// yönlendirir; bu parametreler kanıt DEĞİLDİR. Hosted akışta sonuç
/// <c>GetHostedPaymentStatus</c> sorgusundan okunur; direct akışta 3D dönüşü yalnız
/// doğrulamayı bitirir — para <c>FinishPaymentProcess</c> çağrılmadan bankadan
/// çekilmez, sonucu da o çağrının yanıtı belirler.
///
/// <b>Sipariş numarası:</b> Lidio en çok 20 karakter kabul eder; Poyra'nın deneme kimliği
/// 36 karakterdir. Lidio'ya <see cref="LidioMessages.OrderId"/> ile kısaltılmışı gider,
/// Poyra'ya ise deneme kimliğinin kendisi döner (callback işleyicisi onunla eşleştirir).
///
/// Sorgu kimlikleri (sipariş no, <c>systemTransId</c>) ve tutar başlatma anında
/// KONNEKTÖR DURUMU olarak saklanır. Durum anahtarları <c>poyra_</c> önekli: callback
/// birleştirmesinde tarayıcının alanları kazanır ve Lidio dönüşte <c>OrderId</c> /
/// <c>SystemTransId</c> adlarını kullanır — önek olmasa kendi dönüş parametreleri durumu
/// ezerdi. Önekli anahtarı bilerek enjekte eden biri ise yalnız sorulan siparişi
/// değiştirebilir; sonuç o sipariş numarasıyla döner ve callback işleyicisi deneme
/// kimliğiyle eşleşmeyen siparişi reddeder.
///
/// <b>Doğrulama durumu (Eki 2026):</b> satış, ön provizyon/kapama, iptal, tam ve kısmi iade,
/// 3DS direct, hosted sayfa ve saklı kart akışları Lidio test ortamında uçtan uca koşuldu
/// (<c>tests/Poyra.Tests.Sandbox</c>). Servis adresinin <c>/api</c> öneki ve müşteri tekil
/// alanının müşteri no olduğu orada doğrulandı.
/// <b>TODO(cert):</b> canlı hesapla sertifikasyon ve canlıda API çağrısı yapılan IP'lerin
/// Lidio'ya tanımlatılması.
/// </summary>
public sealed class LidioConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "lidio";
    public const string HttpClientName = "poyra-lidio";

    // Tarayıcının dönüşte gönderdiği OrderId/SystemTransId bunların üzerine yazamasın diye önekli.
    private const string StateOrderId = "poyra_lidio_order_id";
    private const string StateAttemptId = "poyra_lidio_attempt_id";
    private const string StateSystemTransId = "poyra_lidio_system_trans_id";
    private const string StateAmount = "poyra_lidio_amount";
    private const string StateCurrency = "poyra_lidio_currency";
    private const string StateFlow = "poyra_lidio_flow";
    private const string FlowHosted = "hosted";
    private const string FlowDirect = "direct";

    private readonly LidioClient _client = new(httpClientFactory);

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
        Notes: "Hosted ödeme sayfası (PCI kapsamı dışı), 3DS'li ve 3D'siz direct (PCI kapsamı içi) "
               + "birlikte desteklenir. Dönüş parametreleri kanıt sayılmaz: hosted sonuç "
               + "GetHostedPaymentStatus ile sorgulanır, direct'te para FinishPaymentProcess "
               + "ile çekilir. Akışlar Lidio test ortamında doğrulandı; canlıda API çağrısı "
               + "yapılan IP'ler Lidio'ya tanımlatılmalıdır (aksi hâlde InvalidCredential). TODO(cert).");


    public async Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var orderId = LidioMessages.OrderId(request.OrderId);
        var currency = LidioMessages.Currency(request.Currency);

        var response = await _client.StartHostedPaymentAsync(new LidioHostedPayment
        {
            OrderId = orderId,
            AmountMinor = request.AmountMinor,
            Currency = currency,
            Customer = new LidioCustomer(orderId),
            ReturnUrl = request.CallbackUrl,
            Installments = Math.Max(1, request.Installments),
            ClientIp = request.CustomerIp,
        }, credentials, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.RedirectUrl))
            throw new ConnectorUnavailableException(
                $"Lidio ödeme sayfası açılamadı: {response.Result} {response.ResultMessage}");

        // Lidio form değil hazır ADRES döner — GET yönlendirmesi (alan yok).
        return new HostedPaymentForm(
            response.RedirectUrl, new Dictionary<string, string>(), Method: "GET",
            ConnectorState: BuildState(FlowHosted, request.OrderId, orderId, response.SystemTransId,
                request.AmountMinor, currency));
    }


    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var orderId = LidioMessages.OrderId(request.OrderId);
        var currency = LidioMessages.Currency(request.Currency);

        var response = await _client.ProcessPaymentAsync(
            CardPayment(request, orderId, currency, use3DSecure: true, callbackUrl), credentials, ct);

        if (!response.RequiresRedirect
            || ConnectorHtml.ExtractForm(response.RedirectForm ?? string.Empty) is not { } form)
            throw new ConnectorUnavailableException(
                $"Lidio 3D formu dönmedi: {response.Result} {response.ResultDetail} {response.ResultMessage}");

        return new HostedPaymentForm(
            form.ActionUrl, form.Fields,
            ConnectorState: BuildState(FlowDirect, request.OrderId, orderId,
                response.PaymentInfo?.SystemTransId, request.AmountMinor, currency));
    }

    /// <summary>
    /// 3D'siz satış: tek çağrıda sonuçlanır. Kart verisi Poyra'dan geçer (PCI kapsamı);
    /// kart Lidio'da SAKLANMAZ — Poyra'nın kasası varken ikinci bir kart deposu PCI yüküdür.
    /// </summary>
    public async Task<DirectAuthorizeResult?> AuthorizeDirectAsync(
        DirectPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var orderId = LidioMessages.OrderId(request.OrderId);
        var currency = LidioMessages.Currency(request.Currency);

        var response = await _client.ProcessPaymentAsync(
            CardPayment(request, orderId, currency, use3DSecure: false, returnUrl: null), credentials, ct);

        var payment = response.PaymentInfo;
        if (response.IsSuccess)
        {
            if (Mismatch(payment, orderId, request.AmountMinor) is { } mismatch)
                return new DirectAuthorizeResult(false, null, payment?.SystemTransId, null,
                    UnifiedErrors.ProcessingError, null, mismatch);

            return new DirectAuthorizeResult(true, payment?.Pos?.AuthCode, payment?.SystemTransId,
                payment?.Card?.MaskedCardNumber, UnifiedErrors.None, payment?.ResultCategory?.CategoryCode, null);
        }

        // 3D zorunlu hesap/kart 3D'siz isteği RedirectFormCreated ile karşılar; o da başarı
        // değildir — para çekilmedi, işlem hatası olarak döner.
        var declined = Declined(request.OrderId, payment, response.ResultDetail ?? response.Result,
            response.ResultMessage);
        return new DirectAuthorizeResult(false, null, payment?.SystemTransId, null,
            declined.UnifiedCode, declined.RawCode, declined.RawMessage);
    }

    /// <summary>
    /// Dönüş parametreleri imzasız sayılır (3D dönüşündeki hash, hesabın müşteri tekil
    /// alanına bağlı ve tek başına parayı kanıtlamaz). Sonuç her iki akışta da
    /// <see cref="CompleteHostedCallbackAsync"/> içindeki sunucu çağrısından okunur;
    /// burası bu yüzden asla başarı döndürmez.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, Field(form, StateAttemptId) ?? Field(form, StateOrderId) ?? Field(form, "OrderId") ?? string.Empty,
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

        // Deneme kimliği alanı eklenmeden önce başlatılmış denemelerde iki kimlik aynıydı.
        var attemptId = Field(form, StateAttemptId) ?? orderId;

        // systemTransId başlatma yanıtında yoksa dönüşteki değer kullanılır. Bu güvenlidir:
        // sipariş no durumdan geliyor ve Lidio ikisini birlikte eşler — başka bir siparişin
        // işlem numarası bu siparişle sorgulanınca bulunamaz.
        var systemTransId = Field(form, StateSystemTransId) ?? Field(form, "SystemTransId");
        if (string.IsNullOrEmpty(systemTransId))
            return Failed(attemptId, "Lidio işlem numarası (systemTransId) yok; sonuç doğrulanamadı.");

        var amountMinor = long.TryParse(Field(form, StateAmount), CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (long?)null;

        return Field(form, StateFlow) == FlowDirect
            ? await FinishDirectAsync(credentials, attemptId, orderId, systemTransId, amountMinor,
                Field(form, StateCurrency), ct)
            : await QueryHostedAsync(credentials, attemptId, orderId, systemTransId, amountMinor, ct);
    }

    private async Task<HostedCallbackResult> QueryHostedAsync(
        ConnectorCredentials credentials, string attemptId, string orderId, string systemTransId,
        long? amountMinor, CancellationToken ct)
    {
        var response = await _client.GetHostedPaymentStatusAsync(orderId, systemTransId, credentials, ct);

        // Hosted sayfada müşteri reddedilen karttan sonra başka kartla yeniden deneyebilir;
        // liste bu yüzden birden çok deneme taşıyabilir. Aranan, başarılı ve sonradan
        // otomatik iptal EDİLMEMİŞ olanı.
        var payments = response.PaymentList ?? [];
        var succeeded = payments.FirstOrDefault(p => p.IsSuccess == true && p.IsCancelled != true);

        if (response.IsSuccess && LidioMessages.IsApproved(response.PaymentResult) && succeeded is not null)
            return Approved(attemptId, orderId, succeeded, amountMinor);

        return Declined(attemptId, payments.LastOrDefault(), response.PaymentResult ?? response.Result,
            response.ResultMessage);
    }

    private async Task<HostedCallbackResult> FinishDirectAsync(
        ConnectorCredentials credentials, string attemptId, string orderId, string systemTransId,
        long? amountMinor, string? currency, CancellationToken ct)
    {
        // 3D dönüşünün "3DSuccess"/"3DFailed" iddiasına bakılmaz: başarısız doğrulamayı
        // Lidio bu çağrıda zaten ThreeDValidationFailed ile reddeder.
        var response = await _client.FinishPaymentAsync(new LidioFinishPayment
        {
            OrderId = orderId,
            SystemTransId = systemTransId,
            AmountMinor = amountMinor,
            Currency = currency,
        }, credentials, ct);

        return response.IsSuccess
            ? Approved(attemptId, orderId, response.PaymentInfo, amountMinor)
            : Declined(attemptId, response.PaymentInfo, response.ResultDetail ?? response.Result,
                response.ResultMessage);
    }

    private static HostedCallbackResult Approved(
        string attemptId, string orderId, LidioPaymentInfo? payment, long? amountMinor)
    {
        if (Mismatch(payment, orderId, amountMinor) is { } mismatch)
            return Failed(attemptId, mismatch);

        return new HostedCallbackResult(
            true, attemptId,
            AuthCode: payment?.Pos?.AuthCode,
            ConnectorTxnId: payment?.SystemTransId,
            MaskedPan: payment?.Card?.MaskedCardNumber,
            CardBank: payment?.Card?.CardBankName,
            UnifiedErrors.None, payment?.ResultCategory?.CategoryCode, null);
    }

    /// <summary>
    /// Sağlayıcı belgesi açıkça ister: dönen tutar sepet tutarıyla karşılaştırılmalı.
    /// Tutmayan bir "başarı" ya kurcalanmış bir oturumdur ya da bizim hatamız; ikisinde
    /// de ödendi demek yanlış olur — fark mutabakatta yakalanır.
    /// </summary>
    private static string? Mismatch(LidioPaymentInfo? payment, string orderId, long? amountMinor)
    {
        if (amountMinor is { } minor && payment?.AmountRequested is { } lidioAmount
            && lidioAmount != LidioMessages.Amount(minor))
            return $"Lidio tutarı ({lidioAmount.ToString(CultureInfo.InvariantCulture)}) sipariş tutarıyla eşleşmiyor.";

        if (payment?.OrderId is { } returned && returned != orderId)
            return "Lidio yanıtındaki sipariş numarası eşleşmiyor.";

        return null;
    }

    private static HostedCallbackResult Declined(
        string attemptId, LidioPaymentInfo? payment, string? resultCode, string? resultMessage)
    {
        var category = payment?.ResultCategory?.CategoryCode;

        return new HostedCallbackResult(
            false, attemptId, null, null, null, null,
            LidioMessages.UnifiedError(category, resultCode),
            // Ham kod önceliği: bankanın kendi kodu → Lidio kategorisi → Lidio sonucu.
            NonEmpty(payment?.Pos?.ReturnCode) ?? category ?? resultCode,
            NonEmpty(payment?.Pos?.Message) ?? NonEmpty(resultMessage));
    }


    public async Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
    {
        var response = await _client.CancelAsync(LidioMessages.OrderId(reference.OrderId), credentials, ct);
        return Operation(response, "Cancel", reference.ConnectorTxnId, LidioMessages.IsApproved);
    }

    public async Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var response = await _client.RefundAsync(
            LidioMessages.OrderId(request.OrderId), request.AmountMinor, request.Currency,
            request.RefundId is { } refundId ? LidioMessages.OrderId(refundId) : null,
            credentials, ct);
        return Operation(response, "Refund", request.ConnectorTxnId, LidioMessages.IsRefundApproved);
    }

    public async Task<ConnectorProbeResult?> ProbeAsync(ConnectorCredentials credentials, CancellationToken ct)
    {
        try
        {
            // Var olmayan siparişi sorgulamak para hareketi yaratmaz; yanıtın "bulunamadı"
            // olması anahtarın, işyeri kodunun ve IP tanımının geçerli olduğunu gösterir.
            var response = await _client.PaymentInquiryAsync("poyra_canary_000", credentials, ct);

            return response.IsNotAuthorized
                ? new ConnectorProbeResult(false, "Lidio kimlik bilgisi ya da IP tanımı geçersiz.")
                : new ConnectorProbeResult(true, $"Lidio erişilebilir ({response.Result}).");
        }
        catch (Exception ex) when (ex is ConnectorUnavailableException or ConnectorConfigurationException)
        {
            return new ConnectorProbeResult(false, ex.Message);
        }
    }

    // ---- İç yardımcılar --------------------------------------------------------

    private static LidioCardPayment CardPayment(
        DirectPaymentRequest request, string orderId, string currency, bool use3DSecure, string? returnUrl)
        => new()
        {
            OrderId = orderId,
            AmountMinor = request.AmountMinor,
            Currency = currency,
            Customer = new LidioCustomer(orderId),
            Installments = request.Installments,
            Use3DSecure = use3DSecure,
            ReturnUrl = returnUrl,
            ClientIp = request.CustomerIp,
            NewCard = request.Card with { HolderName = request.Card.HolderName ?? "POYRA MUSTERI" },
        };

    private static Dictionary<string, string> BuildState(
        string flow, string attemptId, string orderId, string? systemTransId, long amountMinor, string currency)
    {
        var state = new Dictionary<string, string>
        {
            [StateFlow] = flow,
            [StateOrderId] = orderId,
            [StateAttemptId] = attemptId,
            [StateAmount] = amountMinor.ToString(CultureInfo.InvariantCulture),
            [StateCurrency] = currency,
        };
        if (!string.IsNullOrEmpty(systemTransId))
            state[StateSystemTransId] = systemTransId;
        return state;
    }

    private static ConnectorOperationResult Operation(
        LidioPaymentResponse response, string method, string? originalTxnId, Func<string?, bool> isApproved)
    {
        // İptal/iade kendi işlem numarasını alır; dönmezse asıl işleminki korunur.
        if (isApproved(response.Result))
            return ConnectorOperationResult.Ok(response.PaymentInfo?.SystemTransId ?? originalTxnId);

        var detail = response.ResultDetail ?? response.Result;
        return ConnectorOperationResult.Fail(
            LidioMessages.UnifiedError(null, detail), detail,
            NonEmpty(response.ResultMessage) ?? $"Lidio {method} onaylanmadı ({response.Result}).");
    }

    private static HostedCallbackResult Failed(string orderId, string message)
        => new(false, orderId, null, null, null, null, UnifiedErrors.ProcessingError, null, message);

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Dönüş sorgu dizesinden gelir; anahtarların büyük/küçük harfine güvenilmez.</summary>
    private static string? Field(IReadOnlyDictionary<string, string> form, string name)
        => form.TryGetValue(name, out var value)
            ? value
            : form.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}
