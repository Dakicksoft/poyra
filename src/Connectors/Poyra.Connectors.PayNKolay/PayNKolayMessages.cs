using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.PayNKolay;

/// <summary>
/// PayNKolay mesaj biçimleri ve <c>hashDatav2</c> hesapları.
///
/// İstek ve dönüş hash'leri FARKLI alan listeleri kullanır — ve dönüşteki <c>rnd</c>
/// bizim gönderdiğimiz değil, sağlayıcının ürettiği değerdir. İkisini karıştırmak
/// doğrulamanın hep düşmesine yol açar.
/// </summary>
public static class PayNKolayMessages
{
    /// <summary>Tutar noktalı ondalıkla gider; TR kültürü virgül üretir ve istek reddedilir.</summary>
    public static string Amount(long amountMinor)
        => (amountMinor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    public static string Rastgele() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));

    /// <summary>
    /// İstek hash'i:
    /// <c>sx|clientRefCode|amount|successUrl|failUrl|rnd|customerKey|merchantSecretKey</c>
    /// </summary>
    public static string RequestHash(
        string sx, string clientRefCode, string amount, string successUrl, string failUrl,
        string rnd, string customerKey, string secretKey)
        => Sha512Base64(string.Join('|',
            sx, clientRefCode, amount, successUrl, failUrl, rnd, customerKey, secretKey));

    /// <summary>
    /// Dönüş hash'i:
    /// <c>MERCHANT_NO|REFERENCE_CODE|AUTH_CODE|RESPONSE_CODE|USE_3D|RND|INSTALLMENT|AUTHORIZATION_AMOUNT|CURRENCY_CODE|MERCHANT_SECRET_KEY</c>
    ///
    /// Tutar ve taksit hash'e DAHİLDİR: kurcalanmış bir tutar imzayı düşürür.
    /// </summary>
    public static string ResponseHash(
        string merchantNo, string? referenceCode, string? authCode, string? responseCode,
        string? use3D, string? rnd, string? installment, string? authorizationAmount,
        string? currencyCode, string secretKey)
        => Sha512Base64(string.Join('|',
            merchantNo, referenceCode ?? string.Empty, authCode ?? string.Empty,
            responseCode ?? string.Empty, use3D ?? string.Empty, rnd ?? string.Empty,
            installment ?? string.Empty, authorizationAmount ?? string.Empty,
            currencyCode ?? string.Empty, secretKey));

    /// <summary>Sabit zamanlı karşılaştırma — erken çıkan eşitlik imzayı bayt bayt aratır.</summary>
    public static bool ImzaGecerli(string? gelen, string beklenen)
        => !string.IsNullOrEmpty(gelen)
           && gelen.Length == beklenen.Length
           && CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(gelen), Encoding.UTF8.GetBytes(beklenen));

    /// <summary>Başarı kodu <c>2</c>'dir — "0" ya da "00" değil.</summary>
    public static bool Onaylandi(string? responseCode) => responseCode == "2";

    /// <summary>
    /// Sağlayıcı kodunu Poyra'nın birleşik sözlüğüne çevirir.
    /// <b>TODO(cert):</b> tam liste sağlayıcıdan alınmalı; eşlenmeyen kod terminal sayılır.
    /// </summary>
    public static string UnifiedError(string? responseCode) => responseCode switch
    {
        "2" => UnifiedErrors.None,
        "51" => UnifiedErrors.InsufficientFunds,
        "54" => UnifiedErrors.ExpiredCard,
        "14" or "15" => UnifiedErrors.InvalidCard,
        "57" or "62" => UnifiedErrors.NotPermitted,
        "61" or "65" => UnifiedErrors.LimitExceeded,
        "91" or "96" => UnifiedErrors.IssuerUnavailable,
        null or "" => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined,
    };

    private static string Sha512Base64(string value)
        => Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(value)));
}
