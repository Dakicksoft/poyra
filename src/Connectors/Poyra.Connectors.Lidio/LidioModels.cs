namespace Poyra.Connectors.Lidio;

// Lidio JSON yanıtlarının Poyra'nın kullandığı alt kümesi. Belgedeki şemalar çok geniş
// (kampanya, pazar yeri, e-para…); yalnız okunan alanlar modellenir, gerisi yok sayılır.
// Tutarlar Lidio'nun kendi biçiminde (ondalık, ör. 149.90) bırakılır — kuruşa çeviri
// çağıranın işidir, çünkü tutar karşılaştırması sipariş tutarıyla yapılmalıdır.

/// <summary>Tüm Lidio yanıtlarının ortak sonuç alanları.</summary>
public class LidioResponse
{
    /// <summary>Ana sonuç kodu: <c>Success</c>, <c>InvalidParameter</c>, <c>InvalidCredential</c>…</summary>
    public string? Result { get; init; }

    /// <summary>Ayrıntı kodu (ör. <c>RefTransactionNotFound</c>, <c>CVVRequired</c>). Her uçta yoktur.</summary>
    public string? ResultDetail { get; init; }

    public string? ResultMessage { get; init; }

    public bool IsSuccess => LidioMessages.IsApproved(Result);

    /// <summary>
    /// İşyeri hesabı bu metoda yetkili değil (ya da canlıda IP tanımlı değil). Lidio bunu
    /// kimlik hatasıyla aynı kodla bildirir; ayrım yapılamaz.
    /// </summary>
    public bool IsNotAuthorized => Result == "InvalidCredential";
}

// ---- Ödeme ---------------------------------------------------------------------

public sealed class LidioPaymentResponse : LidioResponse
{
    /// <summary>3D ya da ek doğrulama gerekiyorsa tarayıcıya yazılacak otomatik gönderimli form.</summary>
    public string? RedirectForm { get; init; }

    public LidioPaymentInfo? PaymentInfo { get; init; }

    /// <summary>Yalnız <c>PaymentInquiry</c>: ödemeden bugüne yapılan iadelerin toplamı.</summary>
    public decimal? TotalRefund { get; init; }

    /// <summary>3D formu üretildi; para FinishPaymentProcess çağrılmadan çekilmez.</summary>
    public bool RequiresRedirect => Result == "RedirectFormCreated";

    /// <summary>Saklı kartla ödemede CVV/OTP istendi; FinishPaymentProcess ile tamamlanır.</summary>
    public bool RequiresVerification => Result == "VerificationRequired";
}

public sealed class LidioPaymentInfo
{
    public string? OrderId { get; init; }
    public string? SystemTransId { get; init; }
    public DateTimeOffset? TransactionDate { get; init; }
    public decimal? AmountRequested { get; init; }
    public decimal? AmountProcessed { get; init; }
    public int? InstallmentCount { get; init; }
    public string? Currency { get; init; }
    public string? InstrumentType { get; init; }

    /// <summary>Yalnız hosted durum sorgusunun deneme listesinde dolu gelir.</summary>
    public bool? IsSuccess { get; init; }

    /// <summary>Başarılı ödeme sonradan (ör. fraud kontrolüyle) otomatik iptal edildiyse true.</summary>
    public bool? IsCancelled { get; init; }

    public LidioInstrumentDetail? InstrumentDetail { get; init; }
    public LidioAcquirerResultDetail? AcquirerResultDetail { get; init; }
    public LidioResultCategory? ResultCategory { get; init; }

    public LidioCardDetail? Card => InstrumentDetail?.Card;
    public LidioPosDetail? Pos => AcquirerResultDetail?.Pos;
}

public sealed class LidioInstrumentDetail
{
    public LidioCardDetail? Card { get; init; }
}

public sealed class LidioCardDetail
{
    /// <summary><c>sales</c>, <c>preauth</c>, <c>postauth</c>, <c>cancel</c>, <c>refund</c>.</summary>
    public string? ProcessType { get; init; }

    /// <summary>İptal/iade satırında asıl işlemin türü.</summary>
    public string? RefTransType { get; init; }

    public string? MaskedCardNumber { get; init; }
    public string? CardBankCode { get; init; }
    public string? CardBankName { get; init; }
    public string? CardScheme { get; init; }
    public string? CardToken { get; init; }
    public bool? IsCardSaved { get; init; }
    public bool? Is3DSecure { get; init; }
}

public sealed class LidioAcquirerResultDetail
{
    public LidioPosDetail? Pos { get; init; }
}

public sealed class LidioPosDetail
{
    public int? PosId { get; init; }
    public string? PosBankName { get; init; }
    public string? ReturnCode { get; init; }
    public string? Message { get; init; }
    public string? AuthCode { get; init; }
    public string? TransId { get; init; }
    public string? ReferenceNo { get; init; }
}

public sealed class LidioResultCategory
{
    /// <summary>LD00–LD99; bankanın ham kodunun Lidio tarafından sınıflandırılmış hâli.</summary>
    public string? CategoryCode { get; init; }

    public string? CategoryName { get; init; }
}

// ---- Hosted ödeme sayfası ------------------------------------------------------

public sealed class LidioHostedStartResponse : LidioResponse
{
    public string? SystemTransId { get; init; }
    public string? OrderId { get; init; }

    /// <summary>Müşterinin GET ile yönlendirileceği hazır adres (alan adı Lidio'da <c>redirectURL</c>).</summary>
    public string? RedirectUrl { get; init; }
}

public sealed class LidioHostedStatusResponse : LidioResponse
{
    /// <summary>
    /// Ödemenin kendi sonucu. <c>Result</c> yalnız sorgunun yapılabildiğini söyler;
    /// <c>NewProcess</c> müşterinin henüz ödemediği anlamına gelir.
    /// </summary>
    public string? PaymentResult { get; init; }

    /// <summary>Müşteri sayfada reddedilen karttan sonra yeniden deneyebilir; liste birden çok deneme taşır.</summary>
    public IReadOnlyList<LidioPaymentInfo>? PaymentList { get; init; }
}

// ---- Saklı kart ----------------------------------------------------------------

public sealed class LidioSaveCardResponse : LidioResponse
{
    public string? CardToken { get; init; }
    public string? CardNamebyUser { get; init; }
}

public sealed class LidioUpdateCardResponse : LidioResponse
{
    public string? CardToken { get; init; }
    public string? MaskedCardNumber { get; init; }
    public string? CardNamebyUser { get; init; }
}

public sealed class LidioCardListResponse : LidioResponse
{
    public IReadOnlyList<LidioStoredCard>? CardList { get; init; }
}

public sealed class LidioCardInfoResponse : LidioResponse
{
    /// <summary>Lidio alanı <c>customerID</c> olarak döner.</summary>
    public string? CustomerId { get; init; }

    public LidioStoredCard? CardInfo { get; init; }
}

public sealed class LidioCardTokenResponse : LidioResponse
{
    public string? CardToken { get; init; }
}

public sealed class LidioStoredCard
{
    public string? CardToken { get; init; }
    public string? MaskedCardNumber { get; init; }
    public string? CardHolderName { get; init; }
    public string? CardNamebyUser { get; init; }
    public string? BankCode { get; init; }
    public string? CardType { get; init; }
    public string? BinNumber { get; init; }
    public bool? IsDefault { get; init; }
    public bool? IsDebitCard { get; init; }
    public bool? IsBusinessCard { get; init; }
    public bool? IsExpired { get; init; }

    /// <summary>Kart bu işyerine OTP'yle aktarılmayı bekliyor (Interoperable mod).</summary>
    public bool? FinishPaymentRequired { get; init; }
}

// ---- Kart / taksit bilgisi -----------------------------------------------------

public sealed class LidioBinResponse : LidioResponse
{
    public string? BankCode { get; init; }
    public string? CardType { get; init; }
    public string? CardProgramName { get; init; }
    public bool? IsDebitCard { get; init; }
    public bool? IsBusinessCard { get; init; }
}

public sealed class LidioInstallmentInfoResponse : LidioResponse
{
    public IReadOnlyList<LidioInstallmentPos>? PosList { get; init; }
}

public sealed class LidioInstallmentPos
{
    public int? PosId { get; init; }
    public string? PosBankName { get; init; }
    public string? PosCardPrograms { get; init; }
    public IReadOnlyList<LidioInstallmentOption>? InstallmentOptionList { get; init; }
}

public sealed class LidioInstallmentOption
{
    /// <summary>0 = tek çekim (Lidio tek çekimi 1 değil 0 ile gösterir).</summary>
    public int? InstallmentCount { get; init; }

    public int? ExtraInstallmentCount { get; init; }
    public decimal? InterestRateToUser { get; init; }
    public decimal? TotalAmountWithInterest { get; init; }
}
