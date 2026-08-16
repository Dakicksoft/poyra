using Hangfire;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Recon.Domain;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Recon.Infrastructure;

/// <summary>
/// Takılı eşleştirme süpürmesi: 'matching' durumunda 10+ dk bekleyen ekstreler (iş çökmesi,
/// deploy kesintisi) yeniden kuyruğa atılır. StatementMatcher idempotenttir (sayaçlar
/// sıfırdan, bulgular var-olan-satır atlanır) — tekrar koşmak güvenlidir.
/// recon_statements RLS'lidir → işyeri döngüsü ITenantDirectory ile kurulur.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class ReconStuckSweepJob(
    ReconDbContext db,
    TenantContext tenant,
    ITenantDirectory tenants,
    IBackgroundJobClient jobs,
    IClock clock)
{
    public async Task RequeueStuckAsync()
    {
        var threshold = clock.UtcNow.AddMinutes(-10);

        foreach (var tenantId in await tenants.GetActiveTenantIdsAsync(default))
        {
            tenant.Set(tenantId);

            var stuckIds = await db.ReconStatements.AsNoTracking()
                .Where(s => s.Status == StatementStatus.Matching && s.UpdatedAt < threshold)
                .Select(s => s.Id)
                .ToListAsync();

            foreach (var statementId in stuckIds)
                jobs.Enqueue<StatementMatchJob>(j => j.MatchAsync(tenantId, statementId));
        }
    }
}
