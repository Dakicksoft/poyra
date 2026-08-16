using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.PayFor;

public static class PayForMessages
{
    public static string Amount(long amountMinor)
        => (amountMinor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    public static string Currency(string currency)
        => currency.ToUpperInvariant() switch
        {
            "TRY" or "TL" => "949",
            "USD" => "840",
            "EUR" => "978",
            var digeri => digeri,
        };

    public static string Rastgele() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));

    /// <summary>
    /// İstek hash'i:
    /// <c>MbrId + MrcOrderId + PurchAmount + OkUrl + FailUrl + TxnType + InstallmentCount + Rnd + MerchantPass</c>
    /// </summary>
    public static string RequestHash(
        string mbrId, string orderId, string amount, string okUrl, string failUrl,
        string txnType, string installmentCount, string rnd, string merchantPass)
        => Sha1Base64(string.Concat(
            mbrId, orderId, amount, okUrl, failUrl, txnType, installmentCount, rnd, merchantPass));

    /// <summary>
    /// Dönüş hash'i:
    /// <c>MerchantID + MerchantPass + OrderId + AuthCode + ProcReturnCode + 3DStatus + ResponseRnd + UserCode</c>
    ///
    /// Bankanın uyarısı kritik: 3DModel akışında ödeme henüz gönderilmediği için
    /// <c>AuthCode</c> BOŞ gelir — boş dizeyi atlamak değil, boş olarak hash'e katmak gerekir.
    /// </summary>
    public static string ResponseHash(
        string merchantId, string merchantPass, string orderId, string? authCode,
        string? procReturnCode, string? threeDStatus, string? responseRnd, string userCode)
        => Sha1Base64(string.Concat(
            merchantId, merchantPass, orderId, authCode ?? string.Empty,
            procReturnCode ?? string.Empty, threeDStatus ?? string.Empty,
            responseRnd ?? string.Empty, userCode));

    public static bool ImzaGecerli(string? gelen, string beklenen)
        => !string.IsNullOrEmpty(gelen)
           && gelen.Length == beklenen.Length
           && CryptographicOperations.FixedTimeEquals(
               Encoding.ASCII.GetBytes(gelen), Encoding.ASCII.GetBytes(beklenen));

    public static string UnifiedError(string? procReturnCode, string? threeDStatus)
        => (procReturnCode, threeDStatus) switch
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

    /// <summary>
    /// API yanıtı <c>Ad=Deger;Ad=Deger</c> biçiminde düz metindir (JSON/XML değil).
    /// Boş/bozuk yanıt boş sözlük döner — çağıran "başarısız" sayar (fail closed).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Oku(string govde)
    {
        var sonuc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(govde)) return sonuc;

        foreach (var parca in govde.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var esittir = parca.IndexOf('=');
            if (esittir <= 0) continue;

            sonuc.TryAdd(parca[..esittir].Trim(), parca[(esittir + 1)..].Trim());
        }

        return sonuc;
    }

    private static string Sha1Base64(string value)
        => Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(value)));
}
