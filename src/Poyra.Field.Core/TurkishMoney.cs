using System.Globalization;

namespace Poyra.Field.Core;

/// <summary>
/// Sahada girilen ve gösterilen para.
///
/// <b>Neden çekirdekte, ekranda değil:</b> tutar ayrıştırma sahada para kaybettiren
/// bir yerdir ve test edilebilir olmalıdır.
///
/// <b>Kuruş asla gizlenmez.</b> "1.234,50 ₺" yerine "1.235 ₺" yazmak, temsilcinin gün
/// sonunda kasayı tutturamaması demektir.
/// </summary>
public static class TurkishMoney
{
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>
    /// Girişin tek yolu: <b>yalnız rakam</b>, kuruş sağdan dolar. "123450" → 1.234,50 ₺.
    ///
    /// <b>Neden ayraç kabul edilmiyor — gerçek cihazda bulunan hata:</b> MAUI'nin sayısal
    /// klavyesi (<c>Keyboard.Numeric</c>) Android'de <b>virgülü sessizce yutar</b>, noktayı
    /// kabul eder. Türkiye'de ondalık ayracı virgüldür. Yani temsilci kuruş girmek isteyince
    /// "1234,50" yazamıyor, "1234.50" yazıyordu ve TR okumasında nokta BİNLİK ayracı olduğu
    /// için tutar <b>123.450,00 ₺</b> olarak kaydediliyordu — <b>100 kat</b> sapma, hem de
    /// gün sonu kasa sayımına kadar fark edilmeden.
    ///
    /// Ayracı tamamen kaldırmak belirsizliği kökünden çözer ve Türkiye'deki her POS
    /// terminalinin çalışma şeklidir: temsilci zaten bu alışkanlığa sahiptir.
    /// </summary>
    public static bool TryParseDigits(string? digits, out long minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(digits))
            return false;

        var cleaned = digits.Trim();

        // Tek bir yabancı karakter bile sessizce yok sayılmaz: "12a34" girişi 1234
        // sayılsaydı, temsilcinin gördüğü ile kaydedilen farklı olurdu.
        foreach (var c in cleaned)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        // Uzun girişte taşma: 19 haneden sonrası long'a sığmaz ve sarmalanırsa
        // tutar NEGATİFE düşebilir
        if (cleaned.Length > 18)
            return false;

        minor = long.Parse(cleaned, CultureInfo.InvariantCulture);
        return minor > 0;
    }

    /// <summary>Kuruşu ekranda gösterilecek TR biçimine çevirir — iki hane HER ZAMAN.</summary>
    public static string Format(long minor)
        => (minor / 100m).ToString("N2", Tr) + " ₺";

    /// <summary>
    /// Yazarken canlı gösterim: "1234" → "12,34 ₺". Temsilci ne kaydedeceğini
    /// kaydetmeden ÖNCE görür — 100 kat hatanın ikinci savunma hattı.
    /// </summary>
    public static string Preview(string? digits)
        => TryParseDigits(digits, out var minor) ? Format(minor) : "0,00 ₺";
}
