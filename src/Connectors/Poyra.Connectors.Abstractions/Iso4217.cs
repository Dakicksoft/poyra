namespace Poyra.Connectors.Abstractions;

public static class Iso4217
{
    private static readonly Dictionary<string, string> AlphaToNumeric = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TRY"] = "949",
        ["USD"] = "840",
        ["EUR"] = "978",
        ["GBP"] = "826",
    };

    public static string NumericCode(string alpha3)
        => AlphaToNumeric.TryGetValue(alpha3, out var numeric)
            ? numeric
            : throw new ConnectorConfigurationException($"Desteklenmeyen para birimi: {alpha3}");

    /// <summary>Kuruş → banka biçimi: 149900 → "1499.00" (nokta, invariant).</summary>
    public static string FormatAmount(long amountMinor)
        => (amountMinor / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
