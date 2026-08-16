using System.Globalization;
using System.Text;

namespace Poyra.SharedKernel.Domain;

/// <summary>
/// EMVCo "Merchant-Presented QR" yükü (TLV) ve CRC-16/CCITT-FALSE sağlaması.
/// TR Karekod bu yapının üzerine kuruludur: alanlar 2 haneli etiket + 2 haneli UZUNLUK +
/// değer üçlüsüyle yazılır ve son alan (63) tüm yükün CRC'sidir.
///
/// Uzunluk BAYT değil KARAKTER sayısıdır ve 2 hane sabittir — 99'u aşan değer
/// standarda göre yazılamaz; sessizce kırpmak yerine hata verilir, çünkü kırpılmış
/// bir yük bankada "geçersiz karekod" olarak reddedilir ve müşteri kasada bekler.
/// </summary>
public static class EmvQr
{
    public const string TagPayloadFormat = "00";
    public const string TagInitiationMethod = "01";
    public const string TagMerchantCategory = "52";
    public const string TagCurrency = "53";
    public const string TagAmount = "54";
    public const string TagCountry = "58";
    public const string TagMerchantName = "59";
    public const string TagMerchantCity = "60";
    public const string TagAdditionalData = "62";
    public const string TagCrc = "63";

    /// <summary>Statik karekod: tutar yok, müşteri girer (masa/vitrin etiketi).</summary>
    public const string InitiationStatic = "11";

    /// <summary>Dinamik karekod: tutar yükün içindedir, tek işlem içindir.</summary>
    public const string InitiationDynamic = "12";

    public static string Field(string tag, string value)
    {
        if (tag.Length != 2 || !tag.All(char.IsAsciiDigit))
            throw new ArgumentException($"EMV etiketi iki haneli rakam olmalı: '{tag}'.", nameof(tag));

        if (value.Length > 99)
            throw new ArgumentException(
                $"EMV alanı 99 karakteri aşamaz (etiket {tag}, {value.Length} karakter). "
                + "Kırpmak bankada 'geçersiz karekod' demektir.", nameof(value));

        return $"{tag}{value.Length:00}{value}";
    }

    /// <summary>İç içe şablon (ör. 26 üye işyeri hesabı, 62 ek veri) — alt alanlar birleştirilir.</summary>
    public static string Template(string tag, params (string Tag, string? Value)[] subFields)
    {
        var body = string.Concat(subFields
            .Where(f => !string.IsNullOrEmpty(f.Value))
            .Select(f => Field(f.Tag, f.Value!)));

        return body.Length == 0 ? "" : Field(tag, body);
    }

    /// <summary>
    /// Yükü CRC ile kapatır. CRC, "6304" ÖN EKİ DAHİL hesaplanır (standart böyle der);
    /// dahil etmemek en sık yapılan hatadır ve okuyucu yükü sessizce reddeder.
    /// </summary>
    public static string Seal(string payloadWithoutCrc)
    {
        var withTag = payloadWithoutCrc + TagCrc + "04";
        return withTag + Crc16(withTag).ToString("X4", CultureInfo.InvariantCulture);
    }

    /// <summary>Tutar: nokta ondalık, kuruş iki hane, binlik ayracı YOK (standart gereği).</summary>
    public static string Amount(long amountMinor)
        => (amountMinor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>CRC-16/CCITT-FALSE: poly 0x1021, init 0xFFFF, yansıtma yok, son XOR yok.</summary>
    public static ushort Crc16(string value)
    {
        ushort crc = 0xFFFF;

        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            crc ^= (ushort)(b << 8);
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }

        return crc;
    }

    /// <summary>Okunan bir yükün sağlaması tutuyor mu? (Test ve tanı için.)</summary>
    public static bool Verify(string payload)
    {
        if (payload.Length < 8)
            return false;

        var index = payload.LastIndexOf(TagCrc + "04", StringComparison.Ordinal);
        if (index < 0 || index + 8 != payload.Length)
            return false;

        var expected = Crc16(payload[..(index + 4)]).ToString("X4", CultureInfo.InvariantCulture);
        return string.Equals(payload[(index + 4)..], expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ada/şehre yalnız standardın güvenle taşıdığı karakterler girer. Türkçe harfler
    /// UTF-8'de çok bayttır ve bazı POS okuyucuları uzunluğu BAYT sayar — "Ş" yüzünden
    /// kayan bir yük hiç okunmaz. Bu yüzden ASCII'ye indirgenir (Türkçe eşlemeyle).
    /// </summary>
    public static string Ascii(string value, int maxLength)
    {
        var mapped = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            mapped.Append(ch switch
            {
                'ç' => "c", 'Ç' => "C",
                'ğ' => "g", 'Ğ' => "G",
                'ı' => "i", 'İ' => "I",
                'ö' => "o", 'Ö' => "O",
                'ş' => "s", 'Ş' => "S",
                'ü' => "u", 'Ü' => "U",
                _ => char.IsAscii(ch) ? ch.ToString() : "",
            });
        }

        var result = mapped.ToString().Trim();
        return result.Length <= maxLength ? result : result[..maxLength].Trim();
    }
}
