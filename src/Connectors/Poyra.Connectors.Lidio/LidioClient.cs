using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Lidio;

/// <summary>Lidio müşterisi. Hesabın tekil alanı müşteri no'dur (sandbox'ta doğrulandı).</summary>
public sealed record LidioCustomer(string CustomerId, string? Email = null, string? Name = null, string? Phone = null);

/// <summary>
/// Kartlı ödeme (<c>ProcessPayment</c>). Kart ya yeni (<see cref="NewCard"/>) ya da saklı
/// (<see cref="StoredCardToken"/>) olur — ikisi birden verilemez.
/// </summary>
public sealed record LidioCardPayment
{
    /// <summary>Lidio sipariş no — en çok 20 karakter, yalnız <c>a-zA-Z0-9_</c> (bkz. <see cref="LidioMessages.OrderId"/>).</summary>
    public required string OrderId { get; init; }

    public required long AmountMinor { get; init; }
    public required string Currency { get; init; }
    public required LidioCustomer Customer { get; init; }

    /// <summary><c>sales</c> ya da <c>preauth</c> (ön provizyon; <c>Postauth</c> ile kapanır).</summary>
    public string ProcessType { get; init; } = LidioProcessTypes.Sales;

    public int Installments { get; init; } = 1;
    public bool Use3DSecure { get; init; }

    /// <summary>3D akışında zorunlu: Lidio tarayıcıyı sonuçla buraya GET yönlendirir.</summary>
    public string? ReturnUrl { get; init; }

    public string? ClientIp { get; init; }

    public CardData? NewCard { get; init; }

    /// <summary>Başarılı ödemeden sonra kartı müşteriye kalıcı olarak kaydet.</summary>
    public bool SaveCardAfterSuccess { get; init; }

    public string? StoredCardToken { get; init; }
    public string? StoredCardCvv { get; init; }

    /// <summary>
    /// Saklı kartla ödemede istenen doğrulama (<c>CVV</c>, <c>OTP</c>, <c>OTPandCVV</c>). Boş
    /// liste doğrulamasız ödemedir; dolu listede Lidio <c>VerificationRequired</c> döner ve
    /// ödeme <c>FinishPaymentProcess</c> ile tamamlanır.
    /// </summary>
    public IReadOnlyList<string> VerificationMethods { get; init; } = [];
}

/// <summary>
/// <c>FinishPaymentProcess</c>: 3D dönüşünden, saklı kart doğrulamasından ya da hosted
/// ön ödemeden sonra parayı çeken çağrı.
/// </summary>
public sealed record LidioFinishPayment
{
    public required string OrderId { get; init; }
    public required string SystemTransId { get; init; }

    /// <summary>Verilirse Lidio tutarı başlatılan işlemle karşılaştırır.</summary>
    public long? AmountMinor { get; init; }

    public string? Currency { get; init; }
    public bool StoredCard { get; init; }
    public string? Cvv { get; init; }
    public string? Otp { get; init; }
    public string? ClientIp { get; init; }
}

/// <summary>Hosted ödeme sayfası (<c>StartHostedPaymentProcess</c> / <c>StartHostedPrePaymentProcess</c>).</summary>
public sealed record LidioHostedPayment
{
    public required string OrderId { get; init; }
    public required long AmountMinor { get; init; }
    public required string Currency { get; init; }
    public required LidioCustomer Customer { get; init; }
    public required string ReturnUrl { get; init; }

    public string ProcessType { get; init; } = LidioProcessTypes.Sales;

    /// <summary>
    /// 1'den büyükse sayfada YALNIZ bu taksit açılır (müşteri vade farkını değiştiremez);
    /// 1 tek çekimdir.
    /// </summary>
    public int Installments { get; init; } = 1;

    /// <summary>Sayfada müşterinin saklı kartları da listelensin.</summary>
    public bool AllowStoredCards { get; init; }

    public string? NotificationUrl { get; init; }
    public string? ClientIp { get; init; }
}

public sealed record LidioSaveCard
{
    public required LidioCustomer Customer { get; init; }

    /// <summary>Kart verisi; <see cref="TemporaryCardToken"/> verildiyse boş kalır.</summary>
    public CardData? Card { get; init; }

    /// <summary>15 dakikalık geçici belirteç (ödemede <c>saveCardTemporarily</c> ile üretilir).</summary>
    public string? TemporaryCardToken { get; init; }

    /// <summary>True ise kart 15 dakikalık geçici belirteçle kaydedilir.</summary>
    public bool IsTemporary { get; init; }

    public string? CardNameByUser { get; init; }
    public bool SetAsDefault { get; init; }

    /// <summary>Yalnız Interoperable / telefon doğrulamalı modda: SendOTPForCardSave ile gelen kod.</summary>
    public string? VerificationOtp { get; init; }

    public required string ClientIp { get; init; }
}

public sealed record LidioUpdateCard
{
    public required LidioCustomer Customer { get; init; }
    public required string CardToken { get; init; }

    /// <summary>Lidio son kullanma tarihini her güncellemede ister — değişmese de gönderilir.</summary>
    public required int ExpiryMonth { get; init; }

    public required int ExpiryYear { get; init; }
    public string? CardNameByUser { get; init; }
    public string? CardHolderName { get; init; }
    public bool? SetAsDefault { get; init; }
    public string? VerificationOtp { get; init; }
    public string? ClientIp { get; init; }
}

public static class LidioProcessTypes
{
    public const string Sales = "sales";
    public const string PreAuth = "preauth";
    public const string PostAuth = "postauth";
    public const string Cancel = "cancel";
    public const string Refund = "refund";
}

/// <summary>
/// Lidio REST API'sinin satış, iptal, iade, 3DS, hosted sayfa ve saklı kart metotları.
///
/// Konnektör (<see cref="LidioConnector"/>) Poyra'nın ödeme akışını bunun üzerine kurar;
/// saklı kart ve ön provizyon gibi birleşik arayüzde karşılığı olmayan işlemler doğrudan
/// bu sınıftan çağrılır.
///
/// Sonuç kodu sınıfın işi değildir: her metot Lidio'nun yanıtını olduğu gibi döner
/// (<see cref="LidioResponse.Result"/>). Yalnız taşıma hataları istisnaya çevrilir — 5xx ve
/// ağ hatası <see cref="ConnectorUnavailableException"/>, 401/403 ve eksik kimlik
/// <see cref="ConnectorConfigurationException"/>.
/// </summary>
public sealed class LidioClient(IHttpClientFactory httpClientFactory)
{
    private const string NewCardInstrument = "NewCard";
    private const string StoredCardInstrument = "StoredCard";
    private const string ClientType = "Web";

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ResponseOptions = new(JsonSerializerDefaults.Web)
    {
        // Lidio bazı kimlikleri (systemTransId, posId) uçtan uca sayı ya da metin döndürüyor.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new LenientStringConverter() },
    };

    // ---- PaymentGateway ------------------------------------------------------------

    public Task<LidioPaymentResponse> ProcessPaymentAsync(
        LidioCardPayment payment, ConnectorCredentials credentials, CancellationToken ct)
    {
        if ((payment.NewCard is null) == (payment.StoredCardToken is null))
            throw new ArgumentException("Yeni kart ya da saklı kart belirteci verilmeli (ikisi birden değil).",
                nameof(payment));

        var stored = payment.StoredCardToken is not null;
        var installmentCount = LidioMessages.InstallmentCount(payment.Installments);

        return SendAsync<LidioPaymentResponse>(credentials, "/ProcessPayment", new
        {
            orderId = payment.OrderId,
            merchantProcessId = payment.OrderId,
            totalAmount = LidioMessages.Amount(payment.AmountMinor),
            currency = LidioMessages.Currency(payment.Currency),
            customerInfo = Customer(payment.Customer),
            paymentInstrument = stored ? StoredCardInstrument : NewCardInstrument,
            paymentInstrumentInfo = stored
                ? (object)new
                {
                    storedCard = new
                    {
                        processType = payment.ProcessType,
                        cardToken = payment.StoredCardToken,
                        verificationInfo = new
                        {
                            hostedVerification = false,
                            verificationMethods = payment.VerificationMethods,
                        },
                        cvv = payment.StoredCardCvv,
                        use3DSecure = payment.Use3DSecure,
                        installmentCount,
                        loyaltyPointUsage = "None",
                    },
                }
                : new
                {
                    newCard = new
                    {
                        processType = payment.ProcessType,
                        cardInfo = new
                        {
                            cardHolderName = payment.NewCard!.HolderName,
                            cardNumber = payment.NewCard.Pan,
                            lastMonth = payment.NewCard.ExpiryMonth,
                            lastYear = payment.NewCard.ExpiryYear,
                        },
                        cvv = payment.NewCard.Cvv,
                        use3DSecure = payment.Use3DSecure,
                        installmentCount,
                        loyaltyPointUsage = "None",
                        saveCardTemporarily = false,
                        saveAfterSuccess = payment.SaveCardAfterSuccess,
                    },
                },
            returnUrl = payment.ReturnUrl,
            clientType = ClientType,
            clientIp = payment.ClientIp,
        }, ct);
    }

    public Task<LidioPaymentResponse> FinishPaymentAsync(
        LidioFinishPayment finish, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioPaymentResponse>(credentials, "/FinishPaymentProcess", new
        {
            orderId = finish.OrderId,
            systemTransId = finish.SystemTransId,
            totalAmount = finish.AmountMinor is { } minor ? LidioMessages.Amount(minor) : (decimal?)null,
            currency = finish.Currency is { } currency ? LidioMessages.Currency(currency) : null,
            paymentInstrument = finish.StoredCard ? StoredCardInstrument : NewCardInstrument,
            paymentInstrumentInfo = finish.StoredCard
                ? (object)new { storedCard = new { cvv = finish.Cvv, otp = finish.Otp } }
                : new { newCard = new { } },
            clientType = ClientType,
            clientIp = finish.ClientIp,
        }, ct);

    /// <summary>Ön provizyonu kapatır. Tutar provizyondan küçük olabilir (kısmi kapama).</summary>
    public Task<LidioPaymentResponse> PostauthAsync(
        string orderId, long amountMinor, string currency, ConnectorCredentials credentials, CancellationToken ct,
        bool storedCard = false)
        => SendAsync<LidioPaymentResponse>(credentials, "/Postauth", new
        {
            orderId,
            totalAmount = LidioMessages.Amount(amountMinor),
            currency = LidioMessages.Currency(currency),
            paymentInstrument = Instrument(storedCard),
            clientType = ClientType,
        }, ct);

    /// <summary>
    /// Gün sonu öncesi iptal — satışın ya da ön provizyonun tamamını geri alır.
    /// <paramref name="storedCard"/> asıl ödemenin aracıyla aynı olmalı: saklı kartla yapılan
    /// ödeme NewCard ile arandığında Lidio <c>RefTransactionNotFound</c> döner (sandbox'ta görüldü).
    /// </summary>
    public Task<LidioPaymentResponse> CancelAsync(
        string orderId, ConnectorCredentials credentials, CancellationToken ct, bool storedCard = false)
        => SendAsync<LidioPaymentResponse>(credentials, "/Cancel", new
        {
            orderId,
            paymentInstrument = Instrument(storedCard),
            clientType = ClientType,
        }, ct);

    /// <summary>
    /// Tam ya da kısmi iade. <paramref name="refundTransId"/> verilirse aynı kimlikli ikinci
    /// çağrı yeni iade yapmaz, <c>DuplicateRequest</c> döner (zaman aşımı sonrası güvenli tekrar).
    /// Kimlik verilmezse aynı tutarlı her çağrı AYRI bir iadedir (sandbox'ta doğrulandı).
    /// </summary>
    public Task<LidioPaymentResponse> RefundAsync(
        string orderId, long amountMinor, string currency, string? refundTransId,
        ConnectorCredentials credentials, CancellationToken ct, bool storedCard = false)
        => SendAsync<LidioPaymentResponse>(credentials, "/Refund", new
        {
            orderId,
            totalAmount = LidioMessages.Amount(amountMinor),
            currency = LidioMessages.Currency(currency),
            refundTransId,
            paymentInstrument = Instrument(storedCard),
            clientType = ClientType,
        }, ct);

    /// <summary>İşlem sonucunu Lidio kayıtlarından (gerekirse bankadan) sorgular.</summary>
    public Task<LidioPaymentResponse> PaymentInquiryAsync(
        string orderId, ConnectorCredentials credentials, CancellationToken ct,
        string processType = LidioProcessTypes.Sales)
        => SendAsync<LidioPaymentResponse>(credentials, "/PaymentInquiry", new
        {
            orderId,
            paymentInstrument = NewCardInstrument,
            paymentInquiryInstrumentInfo = new { card = new { processType } },
            clientType = ClientType,
        }, ct);

    /// <summary>İşyerinin POS'ları, taksit seçenekleri ve vade farkları (BIN/tutar verilirse ona göre).</summary>
    public Task<LidioInstallmentInfoResponse> GetInstallmentInfoAsync(
        string? bin, long? amountMinor, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioInstallmentInfoResponse>(credentials, "/GetInstallmentInfo", new
        {
            bin,
            amount = amountMinor is { } minor ? LidioMessages.Amount(minor) : (decimal?)null,
            clientType = ClientType,
        }, ct);

    /// <summary>BIN'in bankası ve kart tipi. Lidio'da ayrıca yetkilendirilmesi gereken bir hizmettir.</summary>
    public Task<LidioBinResponse> GetBankOfBinNumberAsync(
        string bin, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioBinResponse>(credentials, "/GetBankOfBINNumber", new
        {
            bin,
            clientType = ClientType,
        }, ct);

    // ---- HostedPaymentGateway ------------------------------------------------------

    /// <summary>Kart Lidio sayfasında girilir ve ödeme sayfada tamamlanır (PCI kapsamı dışı).</summary>
    public Task<LidioHostedStartResponse> StartHostedPaymentAsync(
        LidioHostedPayment payment, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioHostedStartResponse>(credentials, "/StartHostedPaymentProcess", HostedBody(payment), ct);

    /// <summary>
    /// Kart girişi ve 3D sayfada yapılır ama para ÇEKİLMEZ; müşteri dönüş adresine gelir ve
    /// ödeme <see cref="FinishPaymentAsync"/> ile tamamlanır.
    /// </summary>
    public Task<LidioHostedStartResponse> StartHostedPrePaymentAsync(
        LidioHostedPayment payment, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioHostedStartResponse>(credentials, "/StartHostedPrePaymentProcess", HostedBody(payment), ct);

    public Task<LidioHostedStatusResponse> GetHostedPaymentStatusAsync(
        string orderId, string systemTransId, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioHostedStatusResponse>(credentials, "/GetHostedPaymentStatus", new
        {
            systemTransId,
            orderId,
            clientType = ClientType,
        }, ct);

    /// <summary>Müşterinin saklı kartlarını eklediği/sildiği Lidio sayfası (kart verisi Poyra'ya gelmez).</summary>
    public Task<LidioHostedStartResponse> StartHostedAccountManagementAsync(
        LidioCustomer customer, string returnUrl, bool threeDSecure, ConnectorCredentials credentials,
        CancellationToken ct, string? clientIp = null)
        => SendAsync<LidioHostedStartResponse>(credentials, "/StartHostedAccountManagement", new
        {
            customerInfo = Customer(customer),
            accountInstruments = new[] { "Card" },
            accountInstrumentInfo = new
            {
                card = new
                {
                    processType = LidioProcessTypes.Sales,
                    customerIsLoggedIn = true,
                    threeDSecureMode = threeDSecure ? "Mandatory" : "None",
                },
            },
            returnUrl,
            clientType = ClientType,
            clientIp,
        }, ct);

    // ---- StoredCard ----------------------------------------------------------------

    /// <summary>Yalnız Interoperable / telefon doğrulamalı mod: kart kaydı öncesi SMS kodu.</summary>
    public Task<LidioResponse> SendOtpForCardSaveAsync(
        string phone, ConnectorCredentials credentials, CancellationToken ct, string? clientIp = null)
        => SendAsync<LidioResponse>(credentials, "/SendOTPForCardSave", new
        {
            phone,
            clientType = ClientType,
            clientIp,
        }, ct);

    public Task<LidioSaveCardResponse> SaveCardAsync(
        LidioSaveCard card, ConnectorCredentials credentials, CancellationToken ct)
    {
        if ((card.Card is null) == (card.TemporaryCardToken is null))
            throw new ArgumentException("Kart verisi ya da geçici belirteç verilmeli (ikisi birden değil).",
                nameof(card));

        return SendAsync<LidioSaveCardResponse>(credentials, "/SaveCard", new
        {
            customerInfo = Customer(card.Customer),
            cardHolderName = card.Card?.HolderName,
            cardNumber = card.Card?.Pan,
            cardMonth = card.Card?.ExpiryMonth,
            cardYear = card.Card?.ExpiryYear,
            verificationOtp = card.VerificationOtp,
            temporaryCardToken = card.TemporaryCardToken,
            isTemporary = card.IsTemporary,
            cardNamebyUser = card.CardNameByUser,
            setCardAsDefault = card.SetAsDefault,
            clientType = ClientType,
            clientIp = card.ClientIp,
        }, ct);
    }

    /// <summary>
    /// Kart adı / sahibi / son kullanma tarihi / varsayılan kart güncellemesi. Hiçbir şey
    /// değişmiyorsa Lidio <c>DuplicateCard</c> döner.
    /// </summary>
    public Task<LidioUpdateCardResponse> UpdateCardInfoAsync(
        LidioUpdateCard update, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioUpdateCardResponse>(credentials, "/UpdateCardInfo", new
        {
            customerInfo = Customer(update.Customer),
            cardToken = update.CardToken,
            cardMonth = update.ExpiryMonth,
            cardYear = update.ExpiryYear,
            cardNamebyUser = update.CardNameByUser,
            cardHolderName = update.CardHolderName,
            setCardAsDefault = update.SetAsDefault,
            verificationOtp = update.VerificationOtp,
            clientType = ClientType,
            clientIp = update.ClientIp,
        }, ct);

    /// <summary>Kart yoksa <c>CardNotFound</c> döner (boş liste değil).</summary>
    public Task<LidioCardListResponse> GetCardListAsync(
        string customerId, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioCardListResponse>(credentials, "/GetCardList", new
        {
            customerId,
            clientType = ClientType,
        }, ct);

    /// <summary>Yalnız Interoperable mod: başka işyerinde saklı kartları bu işyerine aktarmak için SMS kodu.</summary>
    public Task<LidioResponse> SendOtpForCardRetrieveAsync(
        string customerId, string phone, ConnectorCredentials credentials, CancellationToken ct,
        string? clientIp = null)
        => SendAsync<LidioResponse>(credentials, "/SendOTPForCardRetrieve", new
        {
            customerId,
            phone,
            clientType = ClientType,
            clientIp,
        }, ct);

    /// <summary>Yalnız Interoperable mod: SMS kodunu doğrular ve aktarılan kartları listeler.</summary>
    public Task<LidioCardListResponse> RetrieveCardsAsync(
        string customerId, string phone, string verificationOtp, ConnectorCredentials credentials,
        CancellationToken ct, string? clientIp = null)
        => SendAsync<LidioCardListResponse>(credentials, "/RetrieveCards", new
        {
            customerId,
            phone,
            verificationOtp,
            clientType = ClientType,
            clientIp,
        }, ct);

    /// <summary>Belirteç ya da maskeli kart numarasıyla tek kart siler.</summary>
    public Task<LidioResponse> DeleteCardAsync(
        string customerId, string cardToken, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioResponse>(credentials, "/DeleteCard", new
        {
            cardToken,
            customerId,
            clientType = ClientType,
        }, ct);

    /// <summary>Müşteri hesabı kapandığında önerilen temizlik: müşterinin bütün kartları.</summary>
    public Task<LidioResponse> DeleteAllCardsOfCustomerAsync(
        string customerId, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioResponse>(credentials, "/DeleteAllCardsOfCustomer", new
        {
            customerId,
            clientType = ClientType,
        }, ct);

    public Task<LidioCardInfoResponse> TokenToCardInfoInquiryAsync(
        string customerId, string cardToken, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioCardInfoResponse>(credentials, "/TokenToCardInfoInquiry", new
        {
            customerId,
            cardToken,
            clientType = ClientType,
        }, ct);

    /// <summary>Kart numarasının saklı belirtecini sorgular. Lidio'da ayrıca yetkilendirilir.</summary>
    public Task<LidioCardTokenResponse> CardToTokenInquiryAsync(
        string cardNumber, ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioCardTokenResponse>(credentials, "/CardToTokenInquiry", new
        {
            cardNumber,
            clientType = ClientType,
        }, ct);

    /// <summary>Bir müşterinin saklı kartını başka bir müşteriye kopyalar. Lidio'da ayrıca yetkilendirilir.</summary>
    public Task<LidioCardTokenResponse> CopyStoredCardAsync(
        string fromCustomerId, string toCustomerId, string cardToken, ConnectorCredentials credentials,
        CancellationToken ct)
        => SendAsync<LidioCardTokenResponse>(credentials, "/CopyStoredCard", new
        {
            fromCustomerId,
            toCustomerId,
            cardToken,
            clientType = ClientType,
        }, ct);

    public Task<LidioResponse> SendOtpForConsentUpdateAsync(
        string customerId, string? phone, ConnectorCredentials credentials, CancellationToken ct,
        string? clientIp = null)
        => SendAsync<LidioResponse>(credentials, "/SendOTPforConsentUpdate", new
        {
            customerId,
            phone,
            clientType = ClientType,
            clientIp,
        }, ct);

    public Task<LidioCardListResponse> UpdateConsentAsync(
        string customerId, string? phone, string? verificationOtp, string clientIp,
        ConnectorCredentials credentials, CancellationToken ct)
        => SendAsync<LidioCardListResponse>(credentials, "/UpdateConsent", new
        {
            customerId,
            phone,
            verificationOtp,
            clientType = ClientType,
            clientIp,
        }, ct);

    /// <summary>Yalnız Interoperable / telefon doğrulamalı mod: kart güncellemesi öncesi SMS kodu.</summary>
    public Task<LidioResponse> SendOtpForCardUpdateAsync(
        string phone, ConnectorCredentials credentials, CancellationToken ct, string? clientIp = null)
        => SendAsync<LidioResponse>(credentials, "/SendOTPForCardUpdate", new
        {
            phone,
            clientType = ClientType,
            clientIp,
        }, ct);

    // ---- Taşıma --------------------------------------------------------------------

    private static object HostedBody(LidioHostedPayment payment)
    {
        var instruments = payment.AllowStoredCards
            ? new[] { NewCardInstrument, StoredCardInstrument }
            : new[] { NewCardInstrument };

        return new
        {
            orderId = payment.OrderId,
            merchantProcessId = payment.OrderId,
            totalAmount = LidioMessages.Amount(payment.AmountMinor),
            currency = LidioMessages.Currency(payment.Currency),
            customerInfo = Customer(payment.Customer),
            paymentInstruments = instruments,
            paymentInstrumentInfo = new
            {
                card = new
                {
                    processType = payment.ProcessType,
                    // Taksit seçeneği müşteriye AÇILMAZ — sayı customParameters ile sabitlenir.
                    useInstallment = false,
                    useLoyaltyPoints = false,
                    newCard = new { threeDSecureMode = "Mandatory", useIVRForCardEntry = false },
                    storedCard = payment.AllowStoredCards
                        ? new
                        {
                            customerIsLoggedIn = true,
                            threeDSecureMode = "Mandatory",
                            verificationMethods = new[] { "CVV" },
                        }
                        : null,
                },
            },
            customParameters = LidioMessages.CustomParameters(payment.Installments),
            returnUrl = payment.ReturnUrl,
            notificationUrl = payment.NotificationUrl,
            clientType = ClientType,
            clientIp = payment.ClientIp,
        };
    }

    private static string Instrument(bool storedCard) => storedCard ? StoredCardInstrument : NewCardInstrument;

    private static object Customer(LidioCustomer customer) => new
    {
        customerId = customer.CustomerId,
        email = customer.Email,
        name = customer.Name,
        phone = customer.Phone,
    };

    private async Task<T> SendAsync<T>(
        ConnectorCredentials credentials, string path, object body, CancellationToken ct)
        where T : LidioResponse
    {
        // Kimlik alanları HTTP'den ÖNCE okunur: eksik alan ağ hatası gibi görünmesin,
        // işyerine "POS bilgilerinizi tamamlayın" olarak dönsün.
        var url = credentials.Require("gateway_base").TrimEnd('/') + path;
        var authorization = LidioMessages.Authorization(credentials.Require("api_key"));
        var merchantCode = credentials.Require("merchant_code");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, RequestOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("MerchantCode", merchantCode);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        string content;
        HttpStatusCode status;
        try
        {
            var client = httpClientFactory.CreateClient(LidioConnector.HttpClientName);
            using var response = await client.SendAsync(request, ct);
            content = await response.Content.ReadAsStringAsync(ct);
            status = response.StatusCode;
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"Lidio {path} ucuna ulaşılamadı.", ex);
        }

        // 4xx gövdesi çoğunlukla sonuç kodunu taşır ve çağıran onu birleşik koda
        // çevirebilmeli; yalnız 5xx/ağ hatası "konnektör ayakta değil" sayılır. 401/403 ise
        // sonuç kodu taşımaz (problem+json) — anahtar ya da metot yetkisi sorunudur.
        if ((int)status >= 500)
            throw new ConnectorUnavailableException($"Lidio {path} → {(int)status}.");
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ConnectorConfigurationException(
                $"Lidio {path} yetkilendirmeyi reddetti ({(int)status}): API anahtarı ya da metot yetkisi.");

        try
        {
            return JsonSerializer.Deserialize<T>(content, ResponseOptions)
                   ?? throw new ConnectorUnavailableException($"Lidio {path} boş yanıt döndü.");
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("Lidio yanıtı JSON değil.", ex);
        }
    }

    /// <summary>Metin alanına sayı gelirse metne çevirir (Lidio kimlikleri iki biçimde de dönebiliyor).</summary>
    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => null,
                _ => throw new JsonException($"Beklenmeyen JSON belirteci: {reader.TokenType}."),
            };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }
}
