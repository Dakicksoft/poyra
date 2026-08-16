using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Routing.Contracts;

namespace Poyra.Modules.Recon.Infrastructure;

public sealed class CommissionRateSource(ReconDbContext db) : ICommissionRateSource
{
    public async Task<IReadOnlyList<ConnectorCommissionRate>> GetRatesAsync(
        int installmentCount, CancellationToken ct)
        => await db.CommissionAgreements.AsNoTracking()
            .Where(a => a.InstallmentCount == installmentCount)
            .Select(a => new ConnectorCommissionRate(a.ConnectorAccountId, a.InstallmentCount, a.RateBps))
            .ToListAsync(ct);
}
