using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Ledger.Contracts;
using Poyra.Modules.Recon.Domain;

namespace Poyra.Modules.Recon.Infrastructure;

/// <summary>
/// Alacak defterinin komisyon/valör kaynağı. Bankaya özel (on-us) anlaşma seçimi
/// <see cref="CommissionAgreementResolver"/> üzerinden — rota ve ekstre denetimiyle aynı yol.
/// </summary>
public sealed class CommissionTerms(ReconDbContext db) : ICommissionTerms
{
    public async Task<CommissionTerm?> FindAsync(
        Guid connectorAccountId, int installments, string? cardBank, CancellationToken ct)
    {
        var agreements = await db.CommissionAgreements.AsNoTracking()
            .Where(a => a.ConnectorAccountId == connectorAccountId
                        && a.InstallmentCount == installments)
            .ToListAsync(ct);

        return CommissionAgreementResolver.Resolve(agreements, installments, cardBank) is { } agreement
            ? new CommissionTerm(agreement.RateBps, agreement.ValorDays)
            : null;
    }
}
