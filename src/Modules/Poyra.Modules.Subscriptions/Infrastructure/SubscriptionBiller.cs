using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Subscriptions.Contracts;
using Poyra.Modules.Subscriptions.Domain;
using Poyra.Modules.Webhooks.Contracts;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Subscriptions.Infrastructure;

/// <summary>
/// Abonelik tahsilat çekirdeği: fatura kes → kartla çek → sonuca göre dönem ilerlet
/// veya dunning kararı uygula. Tahakkuk ve dunning işleri bu sınıfı çağırır (tek yol).
/// İşyeri bağlamı çağıran tarafından kurulmuş olmalıdır (RLS).
/// </summary>
public sealed class SubscriptionBiller(
    SubscriptionsDbContext db,
    IPaymentInitiator payments,
    IWebhookPublisher publisher,
    IClock clock)
{
    public async Task<int> BillDueSubscriptionsAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var due = await db.Subscriptions
            .Where(s => (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing)
                        && s.CurrentPeriodEnd <= now)
            .OrderBy(s => s.CurrentPeriodEnd)
            .Take(200)
            .ToListAsync(ct);

        var billed = 0;
        foreach (var subscription in due)
        {
            if (subscription.CancelAtPeriodEnd)
            {
                subscription.Status = SubscriptionStatus.Cancelled;
                subscription.CancelledAt = now;
                await db.SaveChangesAsync(ct);
                await PublishAsync(subscription, KnownSubscriptionEvents.Cancelled, new { reason = "period_end" }, ct);
                continue;
            }

            var plan = await db.Plans.AsNoTracking().SingleAsync(p => p.Id == subscription.PlanId, ct);

            // Aynı dönem iki kez faturalanmaz (iş tekrar koşsa bile) — unique index de korur
            var periodStart = subscription.CurrentPeriodEnd;
            var existing = await db.SubscriptionInvoices
                .SingleOrDefaultAsync(i => i.SubscriptionId == subscription.Id && i.PeriodStart == periodStart, ct);

            var invoice = existing ?? new SubscriptionInvoice
            {
                TenantId = subscription.TenantId,
                SubscriptionId = subscription.Id,
                PeriodStart = periodStart,
                PeriodEnd = BillingIntervalMap.Advance(periodStart, plan.Interval, plan.IntervalCount),
                AmountMinor = plan.AmountMinor,
                Currency = plan.Currency,
            };
            if (existing is null)
                db.SubscriptionInvoices.Add(invoice);

            await db.SaveChangesAsync(ct); // inv_… üretilsin
            await ChargeInvoiceAsync(subscription, invoice, ct);
            billed++;
        }

        return billed;
    }

    /// <summary>Dunning penceresi gelmiş faturaları yeniden dener.</summary>
    public async Task<int> RetryDueInvoicesAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var due = await db.SubscriptionInvoices
            .Where(i => i.Status == InvoiceStatus.Retrying && i.NextRetryAt != null && i.NextRetryAt <= now)
            .OrderBy(i => i.NextRetryAt)
            .Take(200)
            .ToListAsync(ct);

        var retried = 0;
        foreach (var invoice in due)
        {
            var subscription = await db.Subscriptions.SingleAsync(s => s.Id == invoice.SubscriptionId, ct);

            // Kart güncellemesi bekleniyorsa kör deneme yapma — pencereyi ertele
            if (subscription.NeedsCardUpdate)
            {
                invoice.NextRetryAt = now.AddDays(1);
                await db.SaveChangesAsync(ct);
                continue;
            }

            await ChargeInvoiceAsync(subscription, invoice, ct);
            retried++;
        }

        return retried;
    }

    /// <summary>Tek fatura denemesi + sonuç işleme (tahakkuk ve dunning ortak yolu).</summary>
    public async Task ChargeInvoiceAsync(
        Subscription subscription, SubscriptionInvoice invoice, CancellationToken ct)
    {
        var now = clock.UtcNow;
        invoice.AttemptCount++;

        var result = await payments.ChargeWithTokenAsync(
            invoice.AmountMinor, invoice.Currency, subscription.CardToken,
            $"Abonelik {subscription.PublicId} — dönem {invoice.PeriodStart:yyyy-MM-dd}",
            subscription.CustomerRef, ct);

        invoice.LastPaymentId = result.PaymentId;

        if (result.Success)
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidAt = now;
            invoice.NextRetryAt = null;
            invoice.LastErrorCode = null;

            subscription.CurrentPeriodStart = invoice.PeriodStart;
            subscription.CurrentPeriodEnd = invoice.PeriodEnd;
            subscription.Status = SubscriptionStatus.Active; // trial/past_due → active
            subscription.NeedsCardUpdate = false;

            await db.SaveChangesAsync(ct);
            await PublishAsync(subscription, KnownSubscriptionEvents.InvoicePaid, new
            {
                invoice_id = invoice.PublicId,
                payment_id = result.PaymentId,
                amount_minor = invoice.AmountMinor,
                attempt = invoice.AttemptCount,
            }, ct);
            return;
        }

        invoice.LastErrorCode = result.UnifiedCode;
        var decision = DunningPolicy.Decide(result.UnifiedCode, invoice.AttemptCount, now);

        switch (decision.Action)
        {
            case DunningAction.Retry:
                invoice.Status = InvoiceStatus.Retrying;
                invoice.NextRetryAt = decision.NextAttemptAt;
                subscription.Status = SubscriptionStatus.PastDue;
                break;

            case DunningAction.RequestCardUpdate:
                invoice.Status = InvoiceStatus.Retrying;
                invoice.NextRetryAt = now.AddDays(1); // kart güncellenirse ertesi gün yakalanır
                subscription.Status = SubscriptionStatus.PastDue;
                subscription.NeedsCardUpdate = true;
                break;

            case DunningAction.Abandon:
                invoice.Status = InvoiceStatus.Abandoned;
                invoice.NextRetryAt = null;
                subscription.Status = SubscriptionStatus.Unpaid;
                break;
        }

        await db.SaveChangesAsync(ct);

        await PublishAsync(subscription, KnownSubscriptionEvents.InvoiceFailed, new
        {
            invoice_id = invoice.PublicId,
            payment_id = result.PaymentId,
            error = result.UnifiedCode,
            attempt = invoice.AttemptCount,
            dunning_action = decision.Action.ToString().ToLowerInvariant(),
            next_attempt_at = decision.NextAttemptAt,
            reason = decision.Reason,
        }, ct);

        if (decision.Action == DunningAction.RequestCardUpdate)
            await PublishAsync(subscription, KnownSubscriptionEvents.CardUpdateRequired,
                new { invoice_id = invoice.PublicId, reason = decision.Reason }, ct);

        if (decision.Action == DunningAction.Abandon)
            await PublishAsync(subscription, KnownSubscriptionEvents.Unpaid,
                new { invoice_id = invoice.PublicId, reason = decision.Reason }, ct);
    }

    private Task PublishAsync(Subscription subscription, string eventType, object payload, CancellationToken ct)
        => publisher.PublishAsync(subscription, eventType, payload, ct);
}
