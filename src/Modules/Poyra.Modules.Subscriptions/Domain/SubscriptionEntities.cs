using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Subscriptions.Domain;

public enum BillingInterval
{
    Day,
    Week,
    Month,
    Year,
}

public static class BillingIntervalMap
{
    public static readonly IReadOnlyDictionary<BillingInterval, string> ToDb =
        new Dictionary<BillingInterval, string>
        {
            [BillingInterval.Day] = "day",
            [BillingInterval.Week] = "week",
            [BillingInterval.Month] = "month",
            [BillingInterval.Year] = "year",
        };

    public static readonly IReadOnlyDictionary<string, BillingInterval> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>
    /// Ay/yıl eklemesinde ayın gün sayısı taşarsa ay sonuna kırpılır (31 Ocak + 1 ay = 28/29 Şubat)
    /// — DateTimeOffset.AddMonths semantiği; abonelik "ayın 31'i" tuzağına düşmez.
    /// </summary>
    public static DateTimeOffset Advance(DateTimeOffset from, BillingInterval interval, int count) => interval switch
    {
        BillingInterval.Day => from.AddDays(count),
        BillingInterval.Week => from.AddDays(7 * count),
        BillingInterval.Month => from.AddMonths(count),
        BillingInterval.Year => from.AddYears(count),
        _ => throw new ArgumentOutOfRangeException(nameof(interval)),
    };
}

public sealed class Plan : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public string PublicId { get; private set; } = null!; // pln_…
    public required string Name { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; init; } = "TRY";
    public BillingInterval Interval { get; init; } = BillingInterval.Month;
    public int IntervalCount { get; init; } = 1;
    public int TrialDays { get; init; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum SubscriptionStatus
{
    Trialing,
    Active,
    PastDue, // tahsilat başarısız, dunning sürüyor
    Paused,
    Cancelled,
    Unpaid, // dunning bitti, kurtarılamadı — kayıp
}

public static class SubscriptionStatusMap
{
    public static readonly IReadOnlyDictionary<SubscriptionStatus, string> ToDb =
        new Dictionary<SubscriptionStatus, string>
        {
            [SubscriptionStatus.Trialing] = "trialing",
            [SubscriptionStatus.Active] = "active",
            [SubscriptionStatus.PastDue] = "past_due",
            [SubscriptionStatus.Paused] = "paused",
            [SubscriptionStatus.Cancelled] = "cancelled",
            [SubscriptionStatus.Unpaid] = "unpaid",
        };

    public static readonly IReadOnlyDictionary<string, SubscriptionStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

/// <summary>
/// Abonelik. Kart, Kasa token'ı olarak tutulur (PAN burada YOK) — tekrarlayan tahsilat
/// CVV'siz direct akışla yapılır. İptal edilir, silinmez (İlke 3).
/// </summary>
public sealed class Subscription : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public string PublicId { get; private set; } = null!; // sub_…
    public Guid PlanId { get; init; }
    public required string CustomerRef { get; init; }
    public required string CardToken { get; set; } // tok_… (Kasa)
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public DateTimeOffset CurrentPeriodStart { get; set; }
    public DateTimeOffset CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Kart güncellemesi bekleniyor (expired_card) — müşteriye bildirilir, kör deneme yapılmaz.</summary>
    public bool NeedsCardUpdate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsBillable => Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing;
}

public enum InvoiceStatus
{
    Pending,
    Paid,
    Retrying, // dunning penceresinde
    Abandoned, // deneme hakkı bitti / terminal hata
}

public static class InvoiceStatusMap
{
    public static readonly IReadOnlyDictionary<InvoiceStatus, string> ToDb =
        new Dictionary<InvoiceStatus, string>
        {
            [InvoiceStatus.Pending] = "pending",
            [InvoiceStatus.Paid] = "paid",
            [InvoiceStatus.Retrying] = "retrying",
            [InvoiceStatus.Abandoned] = "abandoned",
        };

    public static readonly IReadOnlyDictionary<string, InvoiceStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

/// <summary>Dönem faturası — her tahsilat denemesi iz bırakır.</summary>
public sealed class SubscriptionInvoice : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public string PublicId { get; private set; } = null!; // inv_…
    public Guid SubscriptionId { get; init; }
    public DateTimeOffset PeriodStart { get; init; }
    public DateTimeOffset PeriodEnd { get; init; }
    public long AmountMinor { get; init; }
    public string Currency { get; init; } = "TRY";
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public string? LastPaymentId { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
