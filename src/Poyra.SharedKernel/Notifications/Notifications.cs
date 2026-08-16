namespace Poyra.SharedKernel.Notifications;

public static class PoyraNotificationTypes
{
    public const string PaymentSucceeded = "payment.succeeded";
    public const string PaymentFailed = "payment.failed";
    public const string PaymentCancelled = "payment.cancelled";
    public const string RefundSucceeded = "refund.succeeded";
    public const string ConnectorHealthChanged = "connector.health";
}

public sealed record PoyraNotification(Guid TenantId, string Type, string? EntityId, string? Detail = null);

public interface INotificationPublisher
{
    Task PublishAsync(PoyraNotification notification, CancellationToken ct = default);
}

public interface INotificationStream
{
    event Action<PoyraNotification>? Received;
}
