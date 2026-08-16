using System.Security.Cryptography;

namespace Poyra.Tests.E2E;

/// <summary>
/// Test tarafı TOTP üreteci (RFC 6238, HMAC-SHA1, 30 sn adım, 6 hane).
///
/// Üretimdeki <c>Totp</c> yalnız DOĞRULAR, üretmez — doğrusu da bu, sunucunun kod
/// üretmesi gerekmiyor. Testin kodu bağımsız üretmesi ayrıca bir kazanç: doğrulayıcıyı
/// kendi ürettiği kodla değil, ondan habersiz ikinci bir uygulamayla sınıyoruz.
/// Doğruluğu RFC 6238 Ek B vektörleriyle sabitlendi (TotpKoduTests).
/// </summary>
public static class TotpKodu
{
    private const string Base32Alfabe = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Verilen anda geçerli 6 haneli kod. Anahtar boşluklu gelebilir (panel 4'erli gösterir).</summary>
    public static string Uret(string base32Anahtar, DateTimeOffset an)
    {
        var anahtar = Base32Coz(base32Anahtar.Replace(" ", "").Replace("-", ""));
        var adim = an.ToUnixTimeSeconds() / 30;

        var sayac = BitConverter.GetBytes(adim);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(sayac);

        var ozet = HMACSHA1.HashData(anahtar, sayac);

        // Dinamik kırpma (RFC 4226 §5.3): son baytın alt 4 biti ofseti verir
        var ofset = ozet[^1] & 0x0F;
        var deger = ((ozet[ofset] & 0x7F) << 24)
                    | (ozet[ofset + 1] << 16)
                    | (ozet[ofset + 2] << 8)
                    | ozet[ofset + 3];

        return (deger % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Coz(string metin)
    {
        var bitler = 0;
        var tampon = 0;
        var cikti = new List<byte>();

        foreach (var karakter in metin.TrimEnd('=').ToUpperInvariant())
        {
            var indeks = Base32Alfabe.IndexOf(karakter);
            if (indeks < 0)
                throw new FormatException($"Base32 dışı karakter: '{karakter}'");

            tampon = (tampon << 5) | indeks;
            bitler += 5;

            if (bitler < 8) continue;

            bitler -= 8;
            cikti.Add((byte)(tampon >> bitler));
        }

        return [.. cikti];
    }
}
