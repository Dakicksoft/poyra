using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.PayTR;


public static class PayTRMessages
{
    public static string Amount(long amountMinor)
        => amountMinor.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// İstek imzası: <c>merchantId + userIp + orderId + email + amount + paymentType +
    /// installment + currency + testMode + non3d + salt</c> → HMAC-SHA256(key) → base64.
    /// </summary>
    public static string RequestToken(
        string merchantId, string userIp, string orderId, string email, string amount,
        string paymentType, string installment, string currency, string testMode,
        string non3d, string merchantKey, string merchantSalt)
        => HmacBase64(string.Concat(
            merchantId, userIp, orderId, email, amount, paymentType, installment,
            currency, testMode, non3d, merchantSalt), merchantKey);

    /// <summary>
    /// Bildirim imzası: <c>orderId + salt + status + totalAmount</c> → HMAC-SHA256 → base64.
    ///
    /// Tutarı ve durumu KAPSAR: kurcalanan bir "success" ya da değiştirilmiş tutar
    /// imzayı düşürür.
    /// </summary>
    public static string NotificationHash(
        string orderId, string status, string totalAmount, string merchantKey, string merchantSalt)
        => HmacBase64(string.Concat(orderId, merchantSalt, status, totalAmount), merchantKey);

    /// <summary>
    /// İade imzası: <c>merchantId + orderId + returnAmount + salt</c> → HMAC-SHA256 → base64.
    /// İstek ve bildirim imzalarından yine farklı bir alan listesi — üçünü ayrı tutmak,
    /// birini diğerinin yerine kullanma hatasını imkânsız kılar.
    /// </summary>
    public static string RefundToken(
        string merchantId, string orderId, string returnAmount, string merchantKey, string merchantSalt)
        => HmacBase64(string.Concat(merchantId, orderId, returnAmount, merchantSalt), merchantKey);

    /// <summary>Sabit zamanlı karşılaştırma — erken çıkan eşitlik imzayı bayt bayt aratır.</summary>
    public static bool ImzaGecerli(string? gelen, string beklenen)
        => !string.IsNullOrEmpty(gelen)
           && gelen.Length == beklenen.Length
           && CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(gelen), Encoding.UTF8.GetBytes(beklenen));

    public static bool Onaylandi(string? status) => status == "success";

    public static string UnifiedError(string? failedReasonCode) => failedReasonCode switch
    {
        "0" => UnifiedErrors.CardDeclined,
        "1" => UnifiedErrors.InsufficientFunds,
        "2" => UnifiedErrors.ThreeDsFailed,
        "3" => UnifiedErrors.ProcessingError,
        "6" => UnifiedErrors.LimitExceeded,
        "8" => UnifiedErrors.InvalidCard,
        "9" => UnifiedErrors.ExpiredCard,
        "10" => UnifiedErrors.NotPermitted,
        "11" => UnifiedErrors.IssuerUnavailable,
        null or "" => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined,
    };

    private static string HmacBase64(string metin, string anahtar)
        => Convert.ToBase64String(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(anahtar), Encoding.UTF8.GetBytes(metin)));
}
