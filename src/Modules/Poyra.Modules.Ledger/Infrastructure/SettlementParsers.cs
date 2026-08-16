using System.Globalization;
using System.Text.RegularExpressions;

namespace Poyra.Modules.Ledger.Infrastructure;

/// <param name="LineNo">Kaynak dosyadaki satır — hata mesajı bunu söylemeli.</param>
/// <param name="AmountMinor">Kuruş, İŞARETLİ: alacak (+), borç (−).</param>
public sealed record ParsedSettlementLine(
    int LineNo, DateOnly ValueDate, long AmountMinor, string? Description, string? Reference);

public sealed record SettlementParseResult(
    IReadOnlyList<ParsedSettlementLine> Lines, IReadOnlyList<string> Errors);

/// <summary>
/// Hesap ekstresi ayrıştırıcısı. Her banka farklı dosya verir; yeni format eklemek
/// bu arayüzü uygulamaktır.
/// </summary>
public interface ISettlementParser
{
    string Format { get; }
    SettlementParseResult Parse(TextReader reader);
}

/// <summary>
/// Kanonik CSV (noktalı virgül — TR Excel varsayılanı):
/// <code>
/// value_date;amount;description;reference
/// 2026-08-05;4523018;GARANTI POS HASILAT;REF123
/// 2026-08-06;-15000;IADE MAHSUBU;
/// </code>
/// Tutar KURUŞ ve işaretlidir; başlık satırı opsiyoneldir.
/// </summary>
public sealed class PoyraCsvSettlementParser : ISettlementParser
{
    public string Format => "poyra_csv";

    public SettlementParseResult Parse(TextReader reader)
    {
        var lines = new List<ParsedSettlementLine>();
        var errors = new List<string>();
        var lineNo = 0;

        while (reader.ReadLine() is { } raw)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var parts = raw.Split(';');
            if (parts.Length < 2)
            {
                errors.Add($"satır {lineNo}: en az value_date;amount gerekir");
                continue;
            }

            // Başlık satırını atla (ilk satırda tarih ayrıştırılamıyorsa)
            if (lineNo == 1 && !DateOnly.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out _))
                continue;

            if (!DateOnly.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var valueDate))
            {
                errors.Add($"satır {lineNo}: valör tarihi ISO (yyyy-MM-dd) olmalı");
                continue;
            }

            if (!long.TryParse(parts[1].Trim(), NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var amount))
            {
                errors.Add($"satır {lineNo}: tutar kuruş cinsinden tam sayı olmalı (işaretli)");
                continue;
            }

            lines.Add(new ParsedSettlementLine(
                lineNo, valueDate, amount,
                parts.Length > 2 ? Trim(parts[2]) : null,
                parts.Length > 3 ? Trim(parts[3]) : null));
        }

        return new SettlementParseResult(lines, errors);
    }

    private static string? Trim(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// <b>MT940</b> — SWIFT hesap ekstresi. Türkiye'de kurumsal bankacılığın fiilî standardı;
/// neredeyse her banka kurumsal müşteriye bu dosyayı verir.
///
/// Bizim için önemli iki etiket:
/// <list type="bullet">
/// <item><c>:61:</c> hareket satırı — valör tarihi, borç/alacak, tutar</item>
/// <item><c>:86:</c> serbest açıklama — hasılatın hangi POS'tan geldiğini yazan yer</item>
/// </list>
///
/// <c>:61:</c> yapısı:
/// <code>
/// :61:2608050805C45230,18NTRFNONREF//BANKREF
///     ^^^^^^          valör YYMMDD
///           ^^^^      kayıt tarihi MMDD (opsiyonel)
///               ^     C=alacak D=borç (RC/RD = ters kayıt)
///                ^^^^^^^^ tutar, ondalık VİRGÜL
///                        ^^^^ işlem tipi (N + 3 harf)
/// </code>
///
/// <b>TODO(cert):</b> bankalar bu alanı biraz farklı doldurur (kayıt tarihi yok, ek
/// alanlar, satır kaydırma). Gerçek dosyalarla banka banka doğrulanmalı; ayrıştırıcı
/// bu yüzden BİLMEDİĞİ satırı sessizce atlamaz, hata olarak bildirir.
/// </summary>
public sealed partial class Mt940SettlementParser : ISettlementParser
{
    public string Format => "mt940";

    [GeneratedRegex(@"^:61:(\d{6})(\d{4})?(RC|RD|C|D)([A-Z])?([\d,\.]+)", RegexOptions.Compiled)]
    private static partial Regex TransactionLine();

    public SettlementParseResult Parse(TextReader reader)
    {
        var lines = new List<ParsedSettlementLine>();
        var errors = new List<string>();
        var lineNo = 0;

        ParsedSettlementLine? pending = null;

        while (reader.ReadLine() is { } raw)
        {
            lineNo++;
            var text = raw.Trim();

            if (text.StartsWith(":86:", StringComparison.Ordinal))
            {
                // Açıklama ÖNCEKİ hareketi anlatır; bekleyen satıra iliştirilir.
                // "GARANTI POS HASILAT" gibi bir metin, farkın nedenini ararken
                // bakılacak ilk yerdir.
                if (pending is not null)
                {
                    lines.Add(pending with { Description = text[4..].Trim() });
                    pending = null;
                }

                continue;
            }

            if (!text.StartsWith(":61:", StringComparison.Ordinal))
                continue;

            // Yeni hareket geldi: açıklamasız kalan öncekini yaz
            if (pending is not null)
            {
                lines.Add(pending);
                pending = null;
            }

            var match = TransactionLine().Match(text);
            if (!match.Success)
            {
                errors.Add($"satır {lineNo}: :61: alanı çözülemedi — banka biçimi farklı olabilir");
                continue;
            }

            if (!TryParseValueDate(match.Groups[1].Value, out var valueDate))
            {
                errors.Add($"satır {lineNo}: valör tarihi (YYMMDD) geçersiz");
                continue;
            }

            if (!TryParseAmount(match.Groups[5].Value, out var amount))
            {
                errors.Add($"satır {lineNo}: tutar çözülemedi");
                continue;
            }

            // D = borç (para çıktı), RC = alacak ters kaydı → ikisi de negatif
            var mark = match.Groups[3].Value;
            var isDebit = mark is "D" or "RC";

            pending = new ParsedSettlementLine(
                lineNo, valueDate, isDebit ? -amount : amount, null, Reference(text));
        }

        if (pending is not null)
            lines.Add(pending);

        return new SettlementParseResult(lines, errors);
    }

    /// <summary>
    /// YYMMDD → tarih. <b>Yüzyıl tuzağı:</b> "26" hem 1926 hem 2026 olabilir. SWIFT
    /// pratiği yakın geçmiş/geleceği varsayar; 70 ve üstü 19xx sayılır. Ekstre
    /// dosyaları güncel olduğu için bu güvenli, ama varsayım olduğu yazılı kalmalı.
    /// </summary>
    private static bool TryParseValueDate(string yymmdd, out DateOnly date)
    {
        date = default;
        if (yymmdd.Length != 6
            || !int.TryParse(yymmdd[..2], out var yy)
            || !int.TryParse(yymmdd[2..4], out var mm)
            || !int.TryParse(yymmdd[4..], out var dd))
            return false;

        var year = yy >= 70 ? 1900 + yy : 2000 + yy;
        if (mm is < 1 or > 12 || dd < 1 || dd > DateTime.DaysInMonth(year, mm))
            return false;

        date = new DateOnly(year, mm, dd);
        return true;
    }

    /// <summary>
    /// MT940 tutarı ondalık ayracı olarak VİRGÜL kullanır (ISO standardı).
    /// Nokta binlik ayracı olabilir; ikisi de temizlenir.
    /// </summary>
    private static bool TryParseAmount(string raw, out long minor)
    {
        minor = 0;
        var cleaned = raw.Replace(".", string.Empty).Replace(',', '.');

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return false;

        minor = (long)Math.Round(value * 100, MidpointRounding.ToEven);
        return true;
    }

    private static string? Reference(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        if (index < 0)
            return null;

        var reference = line[(index + 2)..].Trim();
        return reference.Length == 0 ? null : reference;
    }
}
