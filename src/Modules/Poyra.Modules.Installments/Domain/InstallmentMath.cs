namespace Poyra.Modules.Installments.Domain;

/// <summary>
/// Vade farkı hesabı — kuruş hassasiyetinde, bankacı yuvarlamasıyla (MidpointRounding.ToEven).
/// Kural: yüzdesel hesap decimal'de yapılır, sonuç MUTLAKA long kuruşa döner (İlke: Money).
/// </summary>
public static class InstallmentMath
{
    /// <summary>Toplam = tutar × (1 + bps/10000), kuruşa bankacı yuvarlaması.</summary>
    public static long TotalWithRate(long amountMinor, int rateBps)
        => (long)Math.Round(amountMinor * (1m + rateBps / 10_000m), 0, MidpointRounding.ToEven);

    /// <summary>
    /// Gösterim amaçlı aylık tutar: toplam/n yukarı kuruşa yuvarlanmadan, son ay farkı
    /// kapatacak şekilde: ilk (n-1) ay floor(total/n), son ay kalan. Banka ekstresi de
    /// bu kalıba yakın çalışır; API ikisini de döner.
    /// </summary>
    public static (long Monthly, long LastMonth) MonthlySplit(long totalMinor, int installments)
    {
        if (installments <= 1)
            return (totalMinor, totalMinor);

        var monthly = totalMinor / installments;
        var lastMonth = totalMinor - monthly * (installments - 1);
        return (monthly, lastMonth);
    }
}
