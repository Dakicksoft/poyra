using System.Text.Json;
using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Payments.Domain;


public sealed class OutboxMessage : IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public required string EventType { get; init; }
    public required string PayloadJson { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public int? FanoutCount { get; set; }

    public static OutboxMessage For(Guid tenantId, string eventType, object payload)
        => new()
        {
            TenantId = tenantId,
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(payload),
        };
}
