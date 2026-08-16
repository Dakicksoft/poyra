using System.Globalization;

namespace Poyra.SharedKernel.Domain;


public static class UserInput
{

    public static long ToKurus(string? lira)
    {
        if (string.IsNullOrWhiteSpace(lira))
            return 0;

        var text = lira.Replace(" ", "").Replace(" ", "").Replace("₺", "").Trim();
        if (text.Length == 0)
            return 0;

        var lastComma = text.LastIndexOf(',');
        var lastDot = text.LastIndexOf('.');

        string normalized;
        if (lastComma >= 0 && lastDot >= 0)
        {
            normalized = lastComma > lastDot
                ? text.Replace(".", "").Replace(',', '.')
                : text.Replace(",", "");
        }
        else if (lastComma >= 0)
        {
            normalized = text.Count(c => c == ',') > 1
                ? text.Replace(",", "")
                : text.Replace(',', '.');
        }
        else if (lastDot >= 0)
        {
            var digitsAfterDot = text.Length - lastDot - 1;
            normalized = text.Count(c => c == '.') > 1 || digitsAfterDot == 3
                ? text.Replace(".", "")
                : text;
        }
        else
        {
            normalized = text;
        }

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
               && value > 0
            ? (long)decimal.Truncate(value * 100m)
            : 0;
    }

    public static DateOnly? ToDate(string? value)
        => DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var date) ? date : null;

    public static int ToInt(string? value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;
}
