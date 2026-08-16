using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.CCPayment;

/// <summary>
/// CCPayment altyapısının mesaj biçimleri.
///
/// Bu platformda imza alanı (<c>hash_key</c>) bir ÖZET DEĞİL, geri çözülebilir bir
/// şifredir: alanlar boru işaretiyle birleştirilip AES-256-CBC ile şifrelenir ve dönüşte
/// aynı anahtarla çözülüp içindekiler karşılaştırılır. Doğrulama bu yüzden "hash'i yeniden
/// hesapla ve eşitle" değil, "çöz ve içeriğine bak" biçimindedir.
/// </summary>
public static class CCPaymentMessages
{
    /// <summary>Tutar noktayla ve iki haneli gider — TR kültürü virgül üretir ve istek sessizce reddedilir.</summary>
    public static string Amount(long amountMinor)
        => (amountMinor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// İmza üretir: <c>{iv}:{salt}:{base64}</c>, eğik çizgiler <c>__</c> ile değiştirilir
    /// (değer form alanında ve URL'de taşınabilsin diye).
    ///
    /// IV ve tuz her çağrıda rastgeledir; aynı girdiden aynı imza ÇIKMAZ. Bu yüzden
    /// imza "yeniden üretip eşitleyerek" değil, çözülerek doğrulanır.
    /// </summary>
    public static string Imzala(string birlesikMetin, string appSecret)
    {
        var iv = RastgeleOzet(16);
        var tuz = RastgeleOzet(4);

        using var aes = Aes.Create();
        aes.Key = AnahtarTuret(appSecret, tuz);
        aes.IV = Encoding.UTF8.GetBytes(iv);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var sifreli = aes.EncryptCbc(Encoding.UTF8.GetBytes(birlesikMetin), aes.IV, PaddingMode.PKCS7);
        return $"{iv}:{tuz}:{Convert.ToBase64String(sifreli)}".Replace("/", "__");
    }

    /// <summary>
    /// İmzayı çözer. Bozuk/eksik imza <c>null</c> döner — çağıran bunu "doğrulanamadı"
    /// sayar. İstisna atmak, sahte bir dönüşü 500'e çevirip gerçek hatadan ayırt
    /// edilemez hâle getirirdi.
    /// </summary>
    public static string? Coz(string? imza, string appSecret)
    {
        if (string.IsNullOrWhiteSpace(imza)) return null;

        var parcalar = imza.Replace("__", "/").Split(':');
        if (parcalar.Length != 3) return null;

        var (iv, tuz, sifreli) = (parcalar[0], parcalar[1], parcalar[2]);
        if (iv.Length != 16) return null;

        try
        {
            using var aes = Aes.Create();
            aes.Key = AnahtarTuret(appSecret, tuz);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            var duz = aes.DecryptCbc(
                Convert.FromBase64String(sifreli), Encoding.UTF8.GetBytes(iv), PaddingMode.PKCS7);
            return Encoding.UTF8.GetString(duz);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // yanlış anahtar, bozuk base64 ya da kurcalanmış gövde
        }
    }

    /// <summary>
    /// Anahtar: SHA256(SHA1(appSecret) + tuz) onaltılığının İLK 32 KARAKTERİ, metin
    /// olarak (bayta çevrilmiş hâli değil). Bayt karşılığını kullanmak 32 yerine 16
    /// baytlık anahtar üretir ve platform imzayı reddeder.
    /// </summary>
    private static byte[] AnahtarTuret(string appSecret, string tuz)
        => Encoding.UTF8.GetBytes(OnaltilikOzet(SHA256.HashData(
            Encoding.UTF8.GetBytes(OnaltilikOzet(SHA1.HashData(Encoding.UTF8.GetBytes(appSecret))) + tuz)))[..32]);

    private static string RastgeleOzet(int uzunluk)
        => OnaltilikOzet(SHA1.HashData(
            Encoding.UTF8.GetBytes(RandomNumberGenerator.GetInt32(int.MaxValue).ToString(
                CultureInfo.InvariantCulture))))[..uzunluk];

    private static string OnaltilikOzet(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>
    /// Platform 3D adımını hazır HTML olarak döndürür; Poyra'nın modeli ise
    /// "adres + alanlar" ister (tarayıcıya kendi formumuzu basarız). Form buradan çıkarılır.
    /// </summary>
    public static (string ActionUrl, Dictionary<string, string> Fields)? FormuCikar(string html)
        => ConnectorHtml.FormuCikar(html);

    public static string UnifiedError(string? statusCode, string? mdStatus) => (statusCode, mdStatus) switch
    {
        (_, "0") => UnifiedErrors.ThreeDsFailed,
        (_, "2" or "3" or "4") => UnifiedErrors.ThreeDsUnavailable, // kart 3D'ye kayıtlı değil / katılım yok
        (_, "5" or "6" or "7" or "8") => UnifiedErrors.ThreeDsFailed,
        ("41" or "42", _) => UnifiedErrors.InsufficientFunds,
        ("43", _) => UnifiedErrors.ExpiredCard,
        ("44" or "45", _) => UnifiedErrors.InvalidCard,
        ("46", _) => UnifiedErrors.LimitExceeded,
        (null or "", _) => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined, // eşlenmeyen kod TERMİNAL sayılır: kör yeniden deneme yapılmaz
    };

}
