namespace Poyra.Modules.Webhooks.Contracts;

public static class KnownWebhookEvents
{
    public const string PaymentSucceeded = "payment.succeeded";
    public const string PaymentFailed = "payment.failed";
    public const string PaymentCancelled = "payment.cancelled";
    public const string RefundSucceeded = "refund.succeeded";
    public const string RefundFailed = "refund.failed";

    public const string SubscriptionInvoicePaid = "subscription.invoice.paid";
    public const string SubscriptionInvoiceFailed = "subscription.invoice.failed";
    public const string SubscriptionCardUpdateRequired = "subscription.card_update_required";
    public const string SubscriptionUnpaid = "subscription.unpaid";
    public const string SubscriptionCancelled = "subscription.cancelled";

    public const string DisputeOpened = "dispute.opened";
    public const string DisputeEvidenceDueSoon = "dispute.evidence_due_soon";
    public const string DisputeEvidenceSubmitted = "dispute.evidence_submitted";
    public const string DisputeWon = "dispute.won";
    public const string DisputeLost = "dispute.lost";
    public const string DisputeExpired = "dispute.expired";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        PaymentSucceeded, PaymentFailed, PaymentCancelled, RefundSucceeded, RefundFailed,
        SubscriptionInvoicePaid, SubscriptionInvoiceFailed, SubscriptionCardUpdateRequired,
        SubscriptionUnpaid, SubscriptionCancelled,
        DisputeOpened, DisputeEvidenceDueSoon, DisputeEvidenceSubmitted,
        DisputeWon, DisputeLost, DisputeExpired,
    };
}

public interface IWebhookFanout
{
    Task<int> FanOutAsync(string eventType, string payloadJson, CancellationToken ct);
}
