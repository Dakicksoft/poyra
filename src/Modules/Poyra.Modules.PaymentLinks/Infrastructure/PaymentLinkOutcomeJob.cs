using Hangfire;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.PaymentLinks.Domain;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.PaymentLinks.Infrastructure;

/// <summary>
/// Ödeme bağlantısının sonucunu SUNUCUDA kapatır.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class PaymentLinkOutcomeJob(
    PaymentLinksDbContext db,
    TenantContext tenant,
    ITenantDirectory tenants,
    IPaymentLookup payments,
    IClock clock)
{
    public const int BatchSize = 200;

    public async Task ResolveAsync()
    {
        foreach (var tenantId in await tenants.GetActiveTenantIdsAsync(default))
        {
            tenant.Set(tenantId);
            await ResolveForTenantAsync();
        }
    }

    private async Task ResolveForTenantAsync()
    {
        var settled = db.PaymentLinkUsages.Select(u => u.PaymentPublicId);

        var open = await db.PaymentLinkAttempts.AsNoTracking()
            .Where(a => !settled.Contains(a.PaymentPublicId))
            .OrderBy(a => a.CreatedAt)
            .Take(BatchSize)
            .ToListAsync();

        if (open.Count == 0)
            return;

        var changed = false;

        foreach (var attempt in open)
        {
            var payment = await payments.FindByPublicIdAsync(attempt.PaymentPublicId, default);

            if (payment is not { IsCaptured: true })
                continue;

            var link = await db.PaymentLinks.SingleOrDefaultAsync(l => l.Id == attempt.PaymentLinkId);
            if (link is null)
                continue;

            db.PaymentLinkUsages.Add(new PaymentLinkUsage
            {
                PaymentPublicId = attempt.PaymentPublicId,
                TenantId = attempt.TenantId,
                PaymentLinkId = attempt.PaymentLinkId,
                AmountMinor = attempt.AmountMinor,
            });
            link.SuccessCount++;
            changed = true;
        }

        if (changed)
        {
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
