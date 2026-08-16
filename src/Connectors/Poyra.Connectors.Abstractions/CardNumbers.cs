namespace Poyra.Connectors.Abstractions;

/// <summary>Kart numarası yardımcıları — PAN'a dokunan HER yer PCI kapsamındadır.</summary>
public static class CardNumbers
{
    public static bool IsValidLuhn(string? pan)
    {
        if (pan is null || pan.Length is < 12 or > 19 || !pan.All(char.IsAsciiDigit))
            return false;

        var sum = 0;
        var doubleIt = false;
        for (var i = pan.Length - 1; i >= 0; i--)
        {
            var digit = pan[i] - '0';
            if (doubleIt)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }

            sum += digit;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    /// <summary>İlk 6 + son 4 görünür: 540061******1234 — BIN (6 hane) PCI kapsamı dışıdır.</summary>
    public static string Mask(string pan)
        => pan.Length < 10 ? new string('*', pan.Length) : $"{pan[..6]}{new string('*', pan.Length - 10)}{pan[^4..]}";

    public static string DetectBrand(string pan) => pan switch
    {
        _ when pan.StartsWith("9792") => "troy",
        _ when pan.StartsWith('4') => "visa",
        _ when pan.Length >= 2 && int.TryParse(pan[..2], out var p2) && p2 is >= 51 and <= 55 => "mastercard",
        _ when pan.Length >= 4 && int.TryParse(pan[..4], out var p4) && p4 is >= 2221 and <= 2720 => "mastercard",
        _ when pan.StartsWith("34") || pan.StartsWith("37") => "amex",
        _ => "unknown",
    };
}
