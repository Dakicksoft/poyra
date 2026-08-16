using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Iyzico;

/// <summary>
/// İyzico mesaj biçimleri ve <b>IYZWSv2</b> yetkilendirme başlığı.
///
/// İmza gövdeyi ve YOLU kapsar: aynı gövdeyi başka bir uca göndermek imzayı geçersiz
/// kılar. Rastgele anahtar her istekte değişir ve ayrıca <c>x-iyzi-rnd</c> başlığında
/// gönderilir — sunucu imzayı onunla yeniden hesaplar.
/// </summary>
public static class IyzicoMessages
{
    /// <summary>
    /// İyzico tutarı ondalık NOKTAYLA ve gereksiz sıfırlar olmadan bekler ("149.9").
    /// TR kültürü virgül üretir; virgüllü tutar sessizce reddedilir.
    /// </summary>
    public static string Price(long amountMinor)
    {
        var deger = amountMinor / 100m;
        var metin = deger.ToString("0.00", CultureInfo.InvariantCulture).TrimEnd('0');
        return metin.EndsWith('.') ? metin + "0" : metin;
    }

    /// <summary>Her istekte yeni üretilir; imzaya ve <c>x-iyzi-rnd</c> başlığına girer.</summary>
    public static string RastgeleAnahtar()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    /// <summary>
    /// <c>Authorization</c> başlığının değeri: <c>IYZWSv2 &lt;base64&gt;</c>.
    ///
    /// İmza = HMACSHA256(rastgeleAnahtar + yol + gövde, secretKey) → onaltılık.
    /// Sonra "apiKey:…&amp;randomKey:…&amp;signature:…" dizesi base64'lenir.
    /// Sırayı ya da yolu değiştirmek doğrulamayı kırar.
    /// </summary>
    public static string YetkiBasligi(string apiKey, string secretKey, string yol, string govde, string rastgele)
    {
        var imza = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secretKey),
            Encoding.UTF8.GetBytes(rastgele + yol + govde)));

        var yetki = $"apiKey:{apiKey}&randomKey:{rastgele}&signature:{imza}";
        return "IYZWSv2 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(yetki));
    }

    /// <summary>
    /// 3D adımı base64 kodlu HTML olarak döner; çözülüp içindeki form çıkarılır.
    /// Bozuk base64 <c>null</c> döner — çağıran bunu "sağlayıcı beklenen yanıtı vermedi" sayar.
    /// </summary>
    public static (string ActionUrl, Dictionary<string, string> Fields)? FormuCoz(string? base64Html)
    {
        if (string.IsNullOrWhiteSpace(base64Html)) return null;

        try
        {
            return ConnectorHtml.FormuCikar(Encoding.UTF8.GetString(Convert.FromBase64String(base64Html)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// İyzico hata kodunu Poyra'nın birleşik sözlüğüne çevirir.
    /// <b>TODO(cert):</b> tam liste sağlayıcıdan alınmalı; eşlenmeyen kod terminal sayılır.
    /// </summary>
    public static string UnifiedError(string? errorCode, string? mdStatus) => (errorCode, mdStatus) switch
    {
        (_, "0") => UnifiedErrors.ThreeDsFailed,
        (_, "2" or "3" or "4") => UnifiedErrors.ThreeDsUnavailable, // kart 3D'ye kayıtlı değil
        (_, "5" or "6" or "7" or "8") => UnifiedErrors.ThreeDsFailed,
        ("10051", _) => UnifiedErrors.InsufficientFunds,
        ("10054", _) => UnifiedErrors.ExpiredCard,
        ("10005" or "10012" or "10041", _) => UnifiedErrors.NotPermitted,
        ("10084" or "10057", _) => UnifiedErrors.InvalidCard,
        ("10061" or "10065", _) => UnifiedErrors.LimitExceeded,
        ("10201" or "10202" or "10203", _) => UnifiedErrors.IssuerUnavailable,
        (null or "", _) => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined,
    };
}
