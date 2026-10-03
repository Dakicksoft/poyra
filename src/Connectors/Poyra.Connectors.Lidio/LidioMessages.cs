using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Lidio;

/// <summary>Lidio mesaj biçimleri ve sonuç kategorisi → birleşik kod eşlemesi.</summary>
public static class LidioMessages
{
    /// <summary>
    /// Sunucudan sunucuya anahtar <c>MxS2S</c> önekiyle gider. Diğer önek (<c>MxC2S</c>)
    /// tarayıcıya verilmek üzere üretilen süreli anahtar içindir; Poyra onu kullanmaz.
    /// </summary>
    public static string Authorization(string apiKey) => $"MxS2S {apiKey}";

    /// <summary>
    /// Tutar JSON SAYISI olarak, en çok iki ondalıkla gider. Kuruş göndermek 100 kat
    /// tahsilat demektir.
    /// </summary>
    public static decimal Amount(long amountMinor) => amountMinor / 100m;

    private static readonly HashSet<string> SupportedCurrencies = new(StringComparer.Ordinal)
    {
        "TRY", "USD", "EUR", "GBP", "CHF", "AED", "DKK", "JPY", "KZT", "KWD", "NOK", "RUB", "SEK",
    };

    /// <summary>Lidio'nun kabul ettiği birimler belgelidir; listede olmayanı göndermeden reddederiz.</summary>
    public static string Currency(string currency)
    {
        var code = currency.ToUpperInvariant();
        return SupportedCurrencies.Contains(code)
            ? code
            : throw new ConnectorConfigurationException($"Lidio bu para birimini desteklemiyor: {currency}");
    }

    /// <summary>
    /// Taksit sayısı Poyra'da çoktan karara bağlandı; ödeme sayfasında yalnız o seçenek
    /// açılır ki müşteri başka taksite geçip tahsilat tutarını (vade farkını) değiştiremesin.
    /// Belge gereği bu parametre kullanılırken <c>useInstallment</c> false gönderilir.
    /// </summary>
    public static string? CustomParameters(int installments)
        => installments > 1 ? $"SelectedInstallmentCount:{installments}" : null;

    /// <summary>
    /// Ödemenin sağlayıcıda gerçekten tamamlandığını gösteren tek sonuç. <c>UnexpectedState</c>
    /// ve <c>Pending</c> "belki" demektir — başarı sayılmaz, mutabakatta yakalanır.
    /// </summary>
    public static bool IsApproved(string? result) => result == "Success";

    /// <summary>
    /// <c>DuplicateRequest</c> "bu iade zaten başarıyla yapıldı" demektir; sağlayıcı
    /// belgesi bunu açıkça başarıyla eşdeğer sayar.
    /// </summary>
    public static bool IsRefundApproved(string? result) => result is "Success" or "DuplicateRequest";

    /// <summary>
    /// Lidio bankanın ham kodunu kendi <b>sonuç kategorisine</b> (LD00–LD99) çevirir ve
    /// listeyi yayımlar — bu yüzden eşleme ham banka kodundan değil kategoriden yapılır.
    /// Kategori yoksa (doğrulama hatası, 3D adımı) sonuç/ayrıntı kodlarına düşülür.
    ///
    /// Seçimlerde ölçüt yeniden tahsilat davranışıdır: <see cref="UnifiedErrors.LimitExceeded"/>
    /// ve <see cref="UnifiedErrors.NotPermitted"/> Poyra'da KALICI ret sayılır. Lidio'nun
    /// "bir süre sonra tekrar deneyin" dediği kategoriler (LD10, LD11, LD40) bu yüzden
    /// kart reddine eşlenir; POS tanım hataları (LD12, LD20–LD29) kartın kusuru değildir.
    /// </summary>
    public static string UnifiedError(string? category, string? resultDetail) => category switch
    {
        "LD01" => UnifiedErrors.InsufficientFunds,
        "LD02" => UnifiedErrors.ExpiredCard,
        "LD03" or "LD05" => UnifiedErrors.InvalidCard,
        // Kayıp/çalıntı ve işleme kapalı kart: tekrar denemek kurtarmaz.
        "LD04" or "LD06" or "LD07" or "LD08" or "LD57" => UnifiedErrors.NotPermitted,
        "LD09" or "LD10" or "LD11" or "LD39" or "LD40" or "LD99" => UnifiedErrors.CardDeclined,
        "LD19" => UnifiedErrors.InvalidAmount,
        "LD30" => UnifiedErrors.ThreeDsFailed,
        "LD31" or "LD32" or "LD33" => UnifiedErrors.ThreeDsUnavailable,
        "LD37" => UnifiedErrors.ThreeDsTimeout,
        "LD90" => UnifiedErrors.IssuerUnavailable,
        "LD12" or "LD16" or "LD17" or "LD18" or "LD20" or "LD21" or "LD22" or "LD29" or "LD34" or "LD38"
            => UnifiedErrors.ProcessingError,
        _ => FromResultCode(resultDetail),
    };

    private static string FromResultCode(string? code) => code switch
    {
        "ThreeDValidationFailed" or "UserAuthError" or "Cancelled" => UnifiedErrors.ThreeDsFailed,
        "CardExpired" or "InvalidYear" => UnifiedErrors.ExpiredCard,
        "InvalidCardNum" or "InvalidMonth" or "InvalidCVV" => UnifiedErrors.InvalidCard,
        "InvalidAmount" => UnifiedErrors.InvalidAmount,
        "Refused" => UnifiedErrors.CardDeclined,
        // Kimlik/POS tanımı, sistem hatası, belirsiz sonuç ve tanımadığımız kodlar:
        // kartı suçlamak yerine işlem hatası — yeniden tahsilat geri çekilmeyle dener.
        _ => UnifiedErrors.ProcessingError,
    };
}
