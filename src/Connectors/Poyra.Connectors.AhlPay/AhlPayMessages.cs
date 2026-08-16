using System.Globalization;
using System.Security.Cryptography;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.AhlPay;

public static class AhlPayMessages
{

    public static string Amount(long amountMinor)
        => amountMinor.ToString(CultureInfo.InvariantCulture);

    public static string Rastgele() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));

    /// <summary>
    /// İstek imzası.
    ///
    /// <b>TODO(cert): FORMÜL BİLİNMİYOR.</b> Sağlayıcının kamuya açık dokümanında hash'in
    /// nasıl hesaplandığı yazmıyor; örnek istekte <c>hash</c> ile <c>rnd</c> aynı değerdir.
    /// O davranış burada bilinçli bir YER TUTUCU olarak uygulanmıştır ve
    /// posdestek@ahlpay.com.tr'den alınacak formülle değiştirilmelidir.
    ///
    /// Bunu yer tutucuyla bırakmak güvenlik açığı DEĞİLDİR: bu alan istek tarafındadır,
    /// yanlışsa sağlayıcı isteği reddeder (fail closed). Tahsilatın doğruluğu ise
    /// Bearer belirteçli <c>PaymentInquiry</c> çağrısından okunur — tarayıcı dönüşünden değil.
    /// </summary>
    public static string RequestHash(string rnd) => rnd;


    public static string UnifiedError(string? responseCode) => responseCode switch
    {
        "00" => UnifiedErrors.None,
        "51" => UnifiedErrors.InsufficientFunds,
        "54" => UnifiedErrors.ExpiredCard,
        "14" or "15" => UnifiedErrors.InvalidCard,
        "57" or "62" => UnifiedErrors.NotPermitted,
        "61" or "65" => UnifiedErrors.LimitExceeded,
        "91" or "96" => UnifiedErrors.IssuerUnavailable,
        null or "" => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined,
    };

    public static bool TahsilEdildi(string? txnStatus)
        => txnStatus is "AUTH" or "SUCCESS" or "SALE";
}
