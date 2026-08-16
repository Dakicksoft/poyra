using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Poyra.Modules.Field.Contracts;
using Poyra.Modules.PaymentLinks.Domain;
using Poyra.Modules.PaymentLinks.Features;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.PaymentLinks.Infrastructure;

public sealed class FieldCheckoutLinks(
    PaymentLinksDbContext db,
    TenantContext tenant,
    IClock clock,
    IConfiguration configuration)
    : IFieldCheckoutLinks
{

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    public async Task<FieldCheckoutLink> CreateAsync(
        long amountMinor,
        string currency,
        string description,
        string? customerRef,
        DateTimeOffset? expiresAt,
        CancellationToken ct)
    {
        var link = new PaymentLink
        {
            TenantId = tenant.TenantId,
            Slug = PaymentLink.NewSlug(),
            AmountMinor = amountMinor,
            Currency = currency.ToUpperInvariant(),
            Description = description,
            MaxInstallments = 1,
            ExpiresAt = expiresAt ?? clock.UtcNow.Add(DefaultLifetime),
            MaxUsage = 1,
        };

        db.PaymentLinks.Add(link);
        db.PaymentLinkLookups.Add(new PaymentLinkLookup
        {
            Slug = link.Slug,
            TenantId = link.TenantId,
            PaymentLinkId = link.Id,
        });

        await db.SaveChangesAsync(ct);

        var baseUrl = CreatePaymentLinkHandler.CheckoutBaseUrl(configuration);
        return new FieldCheckoutLink(link.PublicId, $"{baseUrl}/l/{link.Slug}");
    }

    public async Task<FieldLinkOutcome> GetOutcomeAsync(string linkPublicId, CancellationToken ct)
    {
        var link = await db.PaymentLinks.AsNoTracking()
            .Where(l => l.PublicId == linkPublicId)
            .Select(l => new { l.Id, l.SuccessCount, l.ExpiresAt, l.Status })
            .SingleOrDefaultAsync(ct);

        if (link is null)
            return new FieldLinkOutcome(false, null, Expired: true);

        if (link.SuccessCount > 0)
        {
            var paymentId = await db.PaymentLinkUsages.AsNoTracking()
                .Where(u => u.PaymentLinkId == link.Id)
                .OrderBy(u => u.CreatedAt)
                .Select(u => u.PaymentPublicId)
                .FirstOrDefaultAsync(ct);

            return new FieldLinkOutcome(true, paymentId, Expired: false);
        }

        var expired = link.Status != PaymentLinkStatus.Active
                      || (link.ExpiresAt is { } due && due <= clock.UtcNow);

        return new FieldLinkOutcome(false, null, expired);
    }
}
