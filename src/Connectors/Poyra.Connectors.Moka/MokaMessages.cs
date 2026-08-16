using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Moka;

/// <summary>Moka mesaj biçimleri ve <c>CheckKey</c> türetmesi.</summary>
public static class MokaMessages
{
    /// <summary>
    /// Her istekte gönderilen kimlik özeti:
    /// <c>SHA256("{bayiKodu}MK{kullanici}PD{parola}")</c> onaltılık.
    ///
    /// Araya giren "MK" ve "PD" ayraçları önemlidir: onlar olmadan farklı alan
    /// bölünmeleri aynı metni üretebilir (bayi "12"+kullanıcı "34" ile bayi "1"+
    /// kullanıcı "234" ayırt edilemezdi).
    /// </summary>
    public static string CheckKey(string bayiKodu, string kullanici, string parola)
        => Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{bayiKodu}MK{kullanici}PD{parola}")));

    /// <summary>Tutar noktalı ondalıkla gider — TR kültürü virgül üretir ve istek reddedilir.</summary>
    public static string Amount(long amountMinor)
        => (amountMinor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Moka para birimini kendi kısaltmasıyla ister (TRY değil TL).</summary>
    public static string Currency(string currency) => currency.ToUpperInvariant() switch
    {
        "TRY" or "TL" => "TL",
        var digeri => digeri,
    };

    /// <summary>
    /// Moka hata kodunu Poyra'nın birleşik sözlüğüne çevirir.
    /// <b>TODO(cert):</b> tam liste sağlayıcıdan alınmalı; eşlenmeyen kod terminal sayılır.
    /// </summary>
    public static string UnifiedError(string? resultCode) => resultCode switch
    {
        "PaymentDealer.CheckPaymentDealerAuthentication.InvalidRequest"
            or "PaymentDealer.CheckPaymentDealerAuthentication.InvalidAccount"
            => UnifiedErrors.ProcessingError,
        var kod when kod?.Contains("Limit", StringComparison.OrdinalIgnoreCase) == true
            => UnifiedErrors.LimitExceeded,
        var kod when kod?.Contains("Expire", StringComparison.OrdinalIgnoreCase) == true
            => UnifiedErrors.ExpiredCard,
        var kod when kod?.Contains("Insufficient", StringComparison.OrdinalIgnoreCase) == true
            => UnifiedErrors.InsufficientFunds,
        null or "" => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined,
    };
}
