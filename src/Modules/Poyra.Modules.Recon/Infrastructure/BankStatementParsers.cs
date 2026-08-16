namespace Poyra.Modules.Recon.Infrastructure;

public sealed class NestPayCsvStatementParser : IStatementParser
{
    public const string FormatKey = "nestpay_csv";

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
            if (lineNo == 1 && trimmed.StartsWith("ORDER_ID", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = trimmed.Split(';');
            if (parts.Length < 5)
            {
                errors.Add($"satır {lineNo}: en az 5 alan bekleniyor (ORDER_ID;TRAN_TYPE;AMOUNT;COMMISSION;NET[;VALOR])");
                continue;
            }

            var orderId = parts[0].Trim();
            if (orderId.Length == 0)
            {
                errors.Add($"satır {lineNo}: ORDER_ID boş");
                continue;
            }

            // TR 'İ' tuzağı: .NET'te "İ".ToLowerInvariant() harfi DEĞİŞTİRMEZ (U+0130'un
            // tek karakterlik invariant küçük harfi yoktur) — "İade" satırı sessizce
            // satış sayılırdı. Harf bu yüzden açıkça katlanır.
            // ve "iade" ile EŞLEŞMEZ. Deterministik normalize: İ/ı → I/i, sonra invariant upper.
            var tranType = parts[1].Trim().Replace('İ', 'I').Replace('ı', 'i').ToUpperInvariant() switch
            {
                "SATIŞ" or "SATIS" or "SALE" => "sale",
                "IADE" or "REFUND" => "refund",
                _ => null,
            };
            if (tranType is null)
            {
                errors.Add($"satır {lineNo}: TRAN_TYPE 'Satış' veya 'İade' olmalı ('{parts[1].Trim()}')");
                continue;
            }

            if (!TrMoney.TryParseToKurus(parts[2], out var gross)
                || !TrMoney.TryParseToKurus(parts[3], out var commission)
                || !TrMoney.TryParseToKurus(parts[4], out var net))
            {
                errors.Add($"satır {lineNo}: tutarlar TR biçiminde TL olmalı (ör. 1.499,00)");
                continue;
            }

            DateOnly? valor = null;
            if (parts.Length >= 6 && parts[5].Trim() is { Length: > 0 } rawDate)
            {
                if (!DateOnly.TryParseExact(rawDate, "dd.MM.yyyy", out var parsed))
                {
                    errors.Add($"satır {lineNo}: VALOR 'dd.MM.yyyy' biçiminde olmalı ('{rawDate}')");
                    continue;
                }

                valor = parsed;
            }

            lines.Add(new ParsedStatementLine(orderId, tranType, gross, commission, net, valor));
        }

        return new StatementParseResult(lines, errors);
    }
}

public sealed class GvpCsvStatementParser : IStatementParser
{
    public const string FormatKey = "gvp_csv";

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
            if (lineNo == 1 && trimmed.StartsWith("OrderId", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = trimmed.Split(';');
            if (parts.Length < 5)
            {
                errors.Add($"satır {lineNo}: en az 5 alan bekleniyor (OrderId;Type;Gross;Commission;Net[;Valor])");
                continue;
            }

            var orderId = parts[0].Trim();
            if (orderId.Length == 0)
            {
                errors.Add($"satır {lineNo}: OrderId boş");
                continue;
            }

            var type = parts[1].Trim().ToUpperInvariant() switch
            {
                "S" => "sale",
                "I" or "İ" => "refund",
                _ => null,
            };
            if (type is null)
            {
                errors.Add($"satır {lineNo}: Type 'S' (satış) veya 'I' (iade) olmalı ('{parts[1].Trim()}')");
                continue;
            }

            if (!long.TryParse(parts[2].Trim(), out var gross)
                || !long.TryParse(parts[3].Trim(), out var commission)
                || !long.TryParse(parts[4].Trim(), out var net))
            {
                errors.Add($"satır {lineNo}: tutarlar kuruş cinsinden tam sayı olmalı (GVP geleneği)");
                continue;
            }

            DateOnly? valor = null;
            if (parts.Length >= 6 && parts[5].Trim() is { Length: > 0 } rawDate)
            {
                if (!DateOnly.TryParseExact(rawDate, "dd.MM.yyyy", out var parsed))
                {
                    errors.Add($"satır {lineNo}: Valor 'dd.MM.yyyy' biçiminde olmalı ('{rawDate}')");
                    continue;
                }

                valor = parsed;
            }

            lines.Add(new ParsedStatementLine(orderId, type, gross, commission, net, valor));
        }

        return new StatementParseResult(lines, errors);
    }
}
