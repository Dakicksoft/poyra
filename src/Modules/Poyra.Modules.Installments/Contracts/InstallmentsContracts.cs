namespace Poyra.Modules.Installments.Contracts;

public sealed record InstallmentPrice(long ChargedAmountMinor, int RateBps, string SchemeProgram);


public interface IInstallmentPricing
{
    /// <summary>
    /// SÖZLEŞME: dönüşün null'luğu (şema var/yok) tutardan BAĞIMSIZDIR — tutar yalnız müşteri
    /// toplamını etkiler. ExecutionFeasibilitySource kukla tutarla çağırarak buna dayanır;
    /// şemaya tutar limiti eklenecekse önce o bağımlılık çözülmelidir.
    /// </summary>
    Task<InstallmentPrice?> PriceAsync(
        Guid connectorAccountId, int installments, long amountMinor, string? program, CancellationToken ct);
}

/// <param name="BankCode">Kartı çıkaran bankanın kodu — "on-us" (aynı bankanın POS'u) kuralları için.</param>
public sealed record BinInfo(
    string Bin, string BankCode, string BankName, string Program,
    string Brand, string CardType, bool IsCommercial);

/// <summary>
/// Rota motorunun kart sinyali. BIN, kartın ilk 6-8 hanesidir (PAN DEĞİL — PCI kapsamı dışı):
/// hosted akışta checkout formundan, direct akışta PAN'ın ilk hanelerinden gelir.
/// </summary>
public interface IBinLookup
{
    Task<BinInfo?> FindAsync(string bin, CancellationToken ct);
}
