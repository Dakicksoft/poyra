using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Payten;

public static class PaytenMessages
{
    public static string Amount(long amountMinor)
        => (amountMinor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Callback imzası:
    /// <c>SHA512(merchantPaymentId|customerId|sessionToken|responseCode|randomKey|secretKey)</c>.
    ///
    /// <b>TODO(cert):</b> doküman özetin KODLAMASINI (onaltılık mı base64 mü) söylemiyor.
    /// Bu yüzden karşılaştırma her iki gösterimi de kabul eder — ikisi de AYNI özettir,
    /// yani sırrı bilmeyen biri hiçbirini üretemez; gevşeme güvenlikte değil yalnız
    /// biçimdedir. Sertifikasyonda tek biçime sabitlenmeli.
    /// </summary>
    public static bool ImzaGecerli(
        string? gelenImza, string merchantPaymentId, string? customerId, string? sessionToken,
        string? responseCode, string? randomKey, string secretKey)
    {
        if (string.IsNullOrWhiteSpace(gelenImza)) return false;

        var ozet = SHA512.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            merchantPaymentId, customerId ?? string.Empty, sessionToken ?? string.Empty,
            responseCode ?? string.Empty, randomKey ?? string.Empty, secretKey)));

        return SabitZamanliEsit(gelenImza, Convert.ToHexString(ozet))
               || SabitZamanliEsit(gelenImza, Convert.ToHexStringLower(ozet))
               || SabitZamanliEsit(gelenImza, Convert.ToBase64String(ozet));
    }

    /// <summary>
    /// Karşılaştırma sabit zamanlıdır: erken çıkan bir eşitlik kontrolü, saldırganın
    /// imzayı bayt bayt aramasına kapı açar.
    /// </summary>
    private static bool SabitZamanliEsit(string a, string b)
        => a.Length == b.Length
           && CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string UnifiedError(string? responseCode, string? mdStatus) => (responseCode, mdStatus) switch
    {
        (_, "0") => UnifiedErrors.ThreeDsFailed,
        (_, "2" or "3" or "4") => UnifiedErrors.ThreeDsUnavailable,
        (_, "5" or "6" or "7" or "8") => UnifiedErrors.ThreeDsFailed,
        ("51", _) => UnifiedErrors.InsufficientFunds,
        ("54", _) => UnifiedErrors.ExpiredCard,
        ("14" or "15", _) => UnifiedErrors.InvalidCard,
        ("57" or "62", _) => UnifiedErrors.NotPermitted,
        ("61" or "65", _) => UnifiedErrors.LimitExceeded,
        ("91" or "96", _) => UnifiedErrors.IssuerUnavailable,
        (null or "", _) => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined,
    };
}
