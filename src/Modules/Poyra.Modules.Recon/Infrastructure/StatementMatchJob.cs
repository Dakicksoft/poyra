using Hangfire;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Recon.Infrastructure;

/// <summary>
/// Büyük ekstrelerin (SyncMatchLimit üstü) eşleştirmesi — işyeri bağlamı iş parametresinden
/// kurulur (RLS), çekirdek StatementMatcher'la senkron yolla birebir aynıdır.
/// </summary>
[AutomaticRetry(Attempts = 0)] // yarım eşleşme riskine karşı otomatik retry kapalı; F2.3: idempotent süpürme
public sealed class StatementMatchJob(StatementMatcher matcher, TenantContext tenant)
{
    public async Task MatchAsync(Guid tenantId, Guid statementId)
    {
        tenant.Set(tenantId);
        await matcher.MatchAsync(statementId, default);
    }
}
