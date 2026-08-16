namespace Poyra.Modules.Subscriptions.Contracts;

public static class KnownSubscriptionEvents
{
    public const string InvoicePaid = "subscription.invoice.paid";
    public const string InvoiceFailed = "subscription.invoice.failed";
    public const string CardUpdateRequired = "subscription.card_update_required";
    public const string Unpaid = "subscription.unpaid";
    public const string Cancelled = "subscription.cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        InvoicePaid, InvoiceFailed, CardUpdateRequired, Unpaid, Cancelled,
    };
}

public sealed record CustomerSubscription(
    string SubscriptionId, string PlanName, long AmountMinor, string Status,
    DateTimeOffset CurrentPeriodEnd, bool NeedsCardUpdate);

public interface ICustomerSubscriptionSource
{
    Task<IReadOnlyList<CustomerSubscription>> GetSubscriptionsAsync(
        string customerRef, CancellationToken ct);
}
