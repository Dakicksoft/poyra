using System.Text.Json;
using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Payments.Domain;

public sealed class PaymentEvent : ITenantOwned, IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid PaymentIntentId { get; init; }
    public required string EventType { get; init; }
    public required string Actor { get; init; }
    public string Payload { get; init; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }

    public static PaymentEvent For(PaymentIntent intent, string eventType, string actor, object? payload = null)
        => new()
        {
            TenantId = intent.TenantId,
            PaymentIntentId = intent.Id,
            EventType = eventType,
            Actor = actor,
            Payload = payload is null ? "{}" : JsonSerializer.Serialize(payload),
        };
}
