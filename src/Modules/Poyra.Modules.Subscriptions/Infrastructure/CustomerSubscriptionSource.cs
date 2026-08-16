using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Subscriptions.Contracts;
using Poyra.Modules.Subscriptions.Domain;

namespace Poyra.Modules.Subscriptions.Infrastructure;

public sealed class CustomerSubscriptionSource(SubscriptionsDbContext db) : ICustomerSubscriptionSource
{
    public async Task<IReadOnlyList<CustomerSubscription>> GetSubscriptionsAsync(
        string customerRef, CancellationToken ct)
    {
        var rows = await (
            from subscription in db.Subscriptions.AsNoTracking()
            join plan in db.Plans.AsNoTracking() on subscription.PlanId equals plan.Id
            where subscription.CustomerRef == customerRef
            orderby subscription.CreatedAt descending
            select new
            {
                subscription.PublicId,
                plan.Name,
                plan.AmountMinor,
                subscription.Status,
                subscription.CurrentPeriodEnd,
                subscription.NeedsCardUpdate,
            }).ToListAsync(ct);

        return rows.Select(r => new CustomerSubscription(
            r.PublicId, r.Name, r.AmountMinor, SubscriptionStatusMap.ToDb[r.Status],
            r.CurrentPeriodEnd, r.NeedsCardUpdate)).ToList();
    }
}
