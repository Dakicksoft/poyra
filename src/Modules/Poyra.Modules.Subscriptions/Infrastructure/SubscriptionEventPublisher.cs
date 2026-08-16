using System.Text.Json;
using Poyra.Modules.Subscriptions.Domain;
using Poyra.Modules.Webhooks.Contracts;

namespace Poyra.Modules.Subscriptions.Infrastructure;

/// <summary>
/// Abonelik olaylarının işyerine gidiş kapısı. Gövde şekli tek yerde tanımlıdır:
/// { subscription_id, customer_ref, status, data } — tüketici entegrasyonu sabit kalır.
/// </summary>
public interface IWebhookPublisher
{
    Task PublishAsync(Subscription subscription, string eventType, object payload, CancellationToken ct);
}

public sealed class SubscriptionEventPublisher(IWebhookFanout fanout) : IWebhookPublisher
{
    public Task PublishAsync(Subscription subscription, string eventType, object payload, CancellationToken ct)
        => fanout.FanOutAsync(eventType, JsonSerializer.Serialize(new
        {
            subscription_id = subscription.PublicId,
            customer_ref = subscription.CustomerRef,
            status = SubscriptionStatusMap.ToDb[subscription.Status],
            data = payload,
        }), ct);
}
