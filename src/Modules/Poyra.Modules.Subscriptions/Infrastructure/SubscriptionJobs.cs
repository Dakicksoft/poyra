using Hangfire;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Subscriptions.Infrastructure;

/// <summary>
/// Günlük tahakkuk: dönemi dolan abonelikleri faturalar ve tahsil eder.
/// subscriptions RLS'lidir → işyeri döngüsü ITenantDirectory ile kurulur.
/// </summary>
[AutomaticRetry(Attempts = 0)] // tahsilat işi — otomatik retry çift çekim riski taşır
public sealed class SubscriptionBillingJob(
    SubscriptionBiller biller, TenantContext tenant, ITenantDirectory tenants)
{
    public async Task BillAllAsync()
    {
        foreach (var tenantId in await tenants.GetActiveTenantIdsAsync(default))
        {
            tenant.Set(tenantId);
            await biller.BillDueSubscriptionsAsync();
        }
    }
}

/// <summary>Saatlik dunning: penceresi gelen başarısız faturaları yeniden dener.</summary>
[AutomaticRetry(Attempts = 0)]
public sealed class DunningRetryJob(
    SubscriptionBiller biller, TenantContext tenant, ITenantDirectory tenants)
{
    public async Task RetryAllAsync()
    {
        foreach (var tenantId in await tenants.GetActiveTenantIdsAsync(default))
        {
            tenant.Set(tenantId);
            await biller.RetryDueInvoicesAsync();
        }
    }
}
