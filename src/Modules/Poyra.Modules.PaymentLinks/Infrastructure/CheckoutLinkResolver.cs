using Microsoft.EntityFrameworkCore;
using Poyra.Modules.PaymentLinks.Contracts;
using Poyra.Modules.PaymentLinks.Domain;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.PaymentLinks.Infrastructure;

public sealed class CheckoutLinkResolver(
    PaymentLinksDbContext db, TenantContext tenant, IClock clock) : ICheckoutLinkResolver
{
    public async Task<CheckoutLink?> ResolveAsync(string slug, CancellationToken ct)
    {
        var lookup = await db.PaymentLinkLookups.AsNoTracking()
            .SingleOrDefaultAsync(l => l.Slug == slug, ct);
        if (lookup is null)
            return null;

        tenant.Set(lookup.TenantId);

        var link = await db.PaymentLinks.AsNoTracking()
            .SingleOrDefaultAsync(l => l.Id == lookup.PaymentLinkId, ct);
        if (link is null)
            return null;

        return new CheckoutLink(
            link.TenantId, link.Id, link.PublicId, link.Slug, link.AmountMinor, link.Currency,
            link.Description, link.MaxInstallments, link.UnavailableReason(clock.UtcNow),
            link.Origin);
    }


    public async Task RegisterAttemptAsync(
        Guid tenantId, Guid linkId, string paymentPublicId, long amountMinor, CancellationToken ct)
    {
        tenant.Set(tenantId);

        if (await db.PaymentLinkAttempts.AsNoTracking()
                .AnyAsync(a => a.PaymentPublicId == paymentPublicId, ct))
            return;

        db.PaymentLinkAttempts.Add(new PaymentLinkAttempt
        {
            PaymentPublicId = paymentPublicId,
            TenantId = tenantId,
            PaymentLinkId = linkId,
            AmountMinor = amountMinor,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }


    public async Task RegisterSuccessAsync(
        Guid tenantId, Guid linkId, string paymentPublicId, long amountMinor, CancellationToken ct)
    {
        tenant.Set(tenantId);

        if (await db.PaymentLinkUsages.AsNoTracking()
                .AnyAsync(u => u.PaymentPublicId == paymentPublicId, ct))
            return;

        var link = await db.PaymentLinks.SingleOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null)
            return;

        db.PaymentLinkUsages.Add(new PaymentLinkUsage
        {
            PaymentPublicId = paymentPublicId,
            TenantId = tenantId,
            PaymentLinkId = linkId,
            AmountMinor = amountMinor,
        });
        link.SuccessCount++;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }
}
