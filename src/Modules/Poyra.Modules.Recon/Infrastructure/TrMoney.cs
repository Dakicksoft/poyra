using System.Globalization;

namespace Poyra.Modules.Recon.Infrastructure;

/// <summary>
/// Banka dosyalarındaki TR biçimli tutar ("1.499,00" / "1499,5" / "1499") → kuruş.
/// Nokta binlik, virgül ondalıktır; en fazla 2 ondalık hane kabul edilir.
/// </summary>
public static class TrMoney
{
    public static bool TryParseToKurus(string raw, out long kurus)
    {
        kurus = 0;
        var normalized = raw.Trim().Replace(".", "").Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var lira))
            return false;

        var scaled = lira * 100m;
        if (scaled != decimal.Truncate(scaled))
            return false; // 2 haneden fazla ondalık — kuruş altı yok

        kurus = (long)scaled;
        return true;
    }
}
