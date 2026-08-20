using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Recon.Domain;
using Poyra.Modules.Routing.Contracts;

namespace Poyra.Modules.Recon.Infrastructure;

/// <summary>
/// Rota motorunun maliyet sinyali. Kaynak, mutabakatın komisyon anlaşmalarıdır —
/// rotanın kullandığı oran, ay sonunda bankaya itiraz ederken kullanılan oranla AYNIDIR.
/// Bankaya özel (on-us) oran seçimi <see cref="CommissionAgreementResolver"/> üzerinden
/// yapılır: defter ve ekstre denetimi de aynı fonksiyondan geçer.
/// </summary>
public sealed class CommissionRateSource(ReconDbContext db) : ICommissionRateSource
{
    public async Task<IReadOnlyList<ConnectorCommissionRate>> GetRatesAsync(
        int installmentCount, string? cardBank, CancellationToken ct)
    {
        var agreements = await db.CommissionAgreements.AsNoTracking()
            .Where(a => a.InstallmentCount == installmentCount)
            .ToListAsync(ct);

        return agreements
            .GroupBy(a => a.ConnectorAccountId)
            .Select(group => CommissionAgreementResolver.Resolve(group, installmentCount, cardBank))
            .Where(a => a is not null)
            .Select(a => new ConnectorCommissionRate(a!.ConnectorAccountId, a.InstallmentCount, a.RateBps))
            .ToList();
    }
}
