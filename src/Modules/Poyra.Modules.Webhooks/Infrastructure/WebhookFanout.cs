using Hangfire;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Webhooks.Contracts;
using Poyra.Modules.Webhooks.Domain;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Webhooks.Infrastructure;

public sealed class WebhookFanout(
    WebhooksDbContext db, TenantContext tenant, IBackgroundJobClient jobs) : IWebhookFanout
{
    public async Task<int> FanOutAsync(string eventType, string payloadJson, CancellationToken ct)
    {
        var endpoints = await db.WebhookEndpoints
            .Where(e => e.Active && e.EventTypes.Contains(eventType))
            .ToListAsync(ct);

        if (endpoints.Count == 0)
            return 0;

        var deliveries = endpoints
            .Select(e => WebhookDelivery.For(e, eventType, payloadJson))
            .ToList();

        db.WebhookDeliveries.AddRange(deliveries);
        await db.SaveChangesAsync(ct);

        foreach (var delivery in deliveries)
        {
            var tenantId = tenant.TenantId;
            var deliveryId = delivery.Id;
            jobs.Enqueue<WebhookDeliveryJob>(j => j.DeliverAsync(tenantId, deliveryId));
        }

        return deliveries.Count;
    }
}
