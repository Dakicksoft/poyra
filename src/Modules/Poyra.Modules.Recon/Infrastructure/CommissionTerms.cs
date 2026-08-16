using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Ledger.Contracts;

namespace Poyra.Modules.Recon.Infrastructure;

public sealed class CommissionTerms(ReconDbContext db) : ICommissionTerms
{
    public async Task<CommissionTerm?> FindAsync(
        Guid connectorAccountId, int installments, CancellationToken ct)
        => await db.CommissionAgreements.AsNoTracking()
            .Where(a => a.ConnectorAccountId == connectorAccountId
                        && a.InstallmentCount == installments)
            .Select(a => new CommissionTerm(a.RateBps, a.ValorDays))
            .SingleOrDefaultAsync(ct);
}
