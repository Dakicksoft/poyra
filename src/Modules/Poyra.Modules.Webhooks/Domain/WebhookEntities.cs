using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Webhooks.Domain;

public sealed class WebhookEndpoint : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public required string Url { get; set; }
    public byte[] SecretEncrypted { get; set; } = [];
    public string[] EventTypes { get; set; } = [];
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum DeliveryStatus
{
    Pending,
    Succeeded,
    Failed, // yeniden deneme planlandı
    Exhausted, // deneme hakkı bitti — yalnız elle replay
}

public static class DeliveryStatusMap
{
    public static readonly IReadOnlyDictionary<DeliveryStatus, string> ToDb =
        new Dictionary<DeliveryStatus, string>
        {
            [DeliveryStatus.Pending] = "pending",
            [DeliveryStatus.Succeeded] = "succeeded",
            [DeliveryStatus.Failed] = "failed",
            [DeliveryStatus.Exhausted] = "exhausted",
        };

    public static readonly IReadOnlyDictionary<string, DeliveryStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

public sealed class WebhookDelivery : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid EndpointId { get; init; }
    public required string EventType { get; init; }
    public required string PayloadJson { get; init; }
    public DeliveryStatus Status { get; private set; } = DeliveryStatus.Pending;
    public int AttemptCount { get; private set; }
    public DateTimeOffset? NextRetryAt { get; private set; }
    public int? LastHttpStatus { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public Guid? ReplayOfId { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static WebhookDelivery For(WebhookEndpoint endpoint, string eventType, string payloadJson)
        => new()
        {
            TenantId = endpoint.TenantId,
            EndpointId = endpoint.Id,
            EventType = eventType,
            PayloadJson = payloadJson,
        };

    public WebhookDelivery CloneForReplay()
        => new()
        {
            TenantId = TenantId,
            EndpointId = EndpointId,
            EventType = EventType,
            PayloadJson = PayloadJson,
            ReplayOfId = Id,
        };

    public void RegisterSuccess(DateTimeOffset now, int httpStatus)
    {
        AttemptCount++;
        Status = DeliveryStatus.Succeeded;
        LastHttpStatus = httpStatus;
        LastError = null;
        NextRetryAt = null;
        DeliveredAt = now;
    }

    public void RegisterFailure(int? httpStatus, string error, DateTimeOffset? nextRetryAt)
    {
        AttemptCount++;
        LastHttpStatus = httpStatus;
        LastError = error.Length > 500 ? error[..500] : error;
        NextRetryAt = nextRetryAt;
        Status = nextRetryAt is null ? DeliveryStatus.Exhausted : DeliveryStatus.Failed;
    }
}
