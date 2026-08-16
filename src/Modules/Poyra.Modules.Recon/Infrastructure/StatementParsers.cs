using System.Globalization;

namespace Poyra.Modules.Recon.Infrastructure;

public sealed record ParsedStatementLine(
    string OrderId, string LineType, long GrossMinor, long CommissionMinor, long NetMinor, DateOnly? ValueDate);

public sealed record StatementParseResult(
    IReadOnlyList<ParsedStatementLine> Lines, IReadOnlyList<string> Errors);

/// <summary>
/// Ekstre dosya ayrıştırıcısı. Banka-özel formatlar (NestPay/GVP gün sonu dosyaları)
/// bu arayüzle eklenir — format anahtarı yükleme isteğinde seçilir.
/// </summary>
public interface IStatementParser
{
    string Format { get; }
    StatementParseResult Parse(TextReader reader);
}

/// <summary>
/// Kanonik CSV formatı (noktalı virgül — TR Excel varsayılanı):
///   order_id;type;gross_minor;commission_minor;net_minor;value_date
///   att_abc…;sale;10000;250;9750;2026-08-05
///   att_abc…;refund;5000;0;-5000;
/// Başlık satırı opsiyoneldir; type boşsa "sale"; tutarlar KURUŞ (tam sayı);
/// value_date ISO (yyyy-MM-dd) veya boş.
/// </summary>
public sealed class PoyraCsvStatementParser : IStatementParser
{
    public const string FormatKey = "poyra_csv";

    public string Format => FormatKey;

    public StatementParseResult Parse(TextReader reader)
    {
        var lines = new List<ParsedStatementLine>();
        var errors = new List<string>();
        var lineNo = 0;

        while (reader.ReadLine() is { } raw)
        {
            lineNo++;
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
                continue;
            if (lineNo == 1 && trimmed.StartsWith("order_id", StringComparison.OrdinalIgnoreCase))
                continue; // başlık

            var parts = trimmed.Split(';');
            if (parts.Length < 5)
            {
                errors.Add($"satır {lineNo}: en az 5 alan bekleniyor (order_id;type;gross;commission;net[;value_date])");
                continue;
            }

            var orderId = parts[0].Trim();
            var type = parts[1].Trim().ToLowerInvariant();
            if (type.Length == 0)
                type = "sale";

            if (orderId.Length == 0)
            {
                errors.Add($"satır {lineNo}: order_id boş");
                continue;
            }

            if (type is not ("sale" or "refund"))
            {
                errors.Add($"satır {lineNo}: type 'sale' veya 'refund' olmalı ('{parts[1].Trim()}')");
                continue;
            }

            if (!long.TryParse(parts[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var gross)
                || !long.TryParse(parts[3].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var commission)
                || !long.TryParse(parts[4].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var net))
            {
                errors.Add($"satır {lineNo}: tutarlar kuruş cinsinden tam sayı olmalı");
                continue;
            }

            DateOnly? valueDate = null;
            if (parts.Length >= 6 && parts[5].Trim() is { Length: > 0 } rawDate)
            {
                if (!DateOnly.TryParseExact(rawDate, "yyyy-MM-dd", out var parsed))
                {
                    errors.Add($"satır {lineNo}: value_date 'yyyy-MM-dd' biçiminde olmalı ('{rawDate}')");
                    continue;
                }

                valueDate = parsed;
            }

            lines.Add(new ParsedStatementLine(orderId, type, gross, commission, net, valueDate));
        }

        return new StatementParseResult(lines, errors);
    }
}
