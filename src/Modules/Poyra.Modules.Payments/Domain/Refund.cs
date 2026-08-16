using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Payments.Domain;

public enum RefundStatus
{
    Pending,
    Succeeded,
    Failed,
}

public static class RefundStatusMap
{
    public static readonly IReadOnlyDictionary<RefundStatus, string> ToDb =
        new Dictionary<RefundStatus, string>
        {
            [RefundStatus.Pending] = "pending",
            [RefundStatus.Succeeded] = "succeeded",
            [RefundStatus.Failed] = "failed",
        };

    public static readonly IReadOnlyDictionary<string, RefundStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

public sealed class Refund : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public string PublicId { get; private set; } = null!; // ref_…
    public Guid PaymentIntentId { get; init; }
    public Guid PaymentAttemptId { get; init; }
    public long AmountMinor { get; init; }
    public string Currency { get; init; } = "TRY";
    public RefundStatus Status { get; private set; } = RefundStatus.Pending;
    public string? ConnectorRefundId { get; private set; }
    public string? ErrorUnifiedCode { get; private set; }
    public string? ErrorRawCode { get; private set; }
    public string? ErrorRawMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static Refund Open(PaymentIntent intent, PaymentAttempt attempt, long amountMinor)
        => new()
        {
            TenantId = intent.TenantId,
            PaymentIntentId = intent.Id,
            PaymentAttemptId = attempt.Id,
            AmountMinor = amountMinor,
            Currency = intent.Currency,
        };

    public void MarkSucceeded(string? connectorRefundId)
    {
        Status = RefundStatus.Succeeded;
        ConnectorRefundId = connectorRefundId;
    }

    public void MarkFailed(string? unifiedCode, string? rawCode, string? rawMessage)
    {
        Status = RefundStatus.Failed;
        ErrorUnifiedCode = unifiedCode;
        ErrorRawCode = rawCode;
        ErrorRawMessage = rawMessage;
    }
}
