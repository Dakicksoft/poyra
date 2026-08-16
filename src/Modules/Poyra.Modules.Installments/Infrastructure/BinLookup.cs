using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Installments.Contracts;

namespace Poyra.Modules.Installments.Infrastructure;

public sealed class BinLookup(InstallmentsDbContext db) : IBinLookup
{
    public async Task<BinInfo?> FindAsync(string bin, CancellationToken ct)
    {
        if (bin.Length < 6)
            return null;

        // En uzun eşleşme kazanır: 8 haneli özel kayıt, 6'lık genel kayda tercih edilir
        var candidates = new[] { bin[..Math.Min(8, bin.Length)], bin[..6] }.Distinct().ToArray();

        return await db.CardBins.AsNoTracking()
            .Where(b => candidates.Contains(b.Bin))
            .OrderByDescending(b => b.Bin.Length)
            .Select(b => new BinInfo(
                b.Bin, b.BankCode, b.BankName, b.Program, b.Brand, b.CardType, b.IsCommercial))
            .FirstOrDefaultAsync(ct);
    }
}
