using Hangfire;
using Microsoft.EntityFrameworkCore;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Webhooks.Contracts;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Payments.Infrastructure;

[AutomaticRetry(Attempts = 0)]
public sealed class ThreeDsTimeoutJob(PaymentsDbContext db, TenantContext tenant, IClock clock)
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    public async Task ExpireStaleAsync()
    {
        var threshold = clock.UtcNow - Grace;

        var staleTokens = await db.CallbackTokens
            .Where(t => t.UsedAt == null && t.ExpiresAt < threshold)
            .OrderBy(t => t.ExpiresAt)
            .Take(100)
            .ToListAsync();

        foreach (var token in staleTokens)
        {
            tenant.Set(token.TenantId);

            var intent = await db.PaymentIntents.SingleAsync(p => p.Id == token.PaymentIntentId);
            var attempt = await db.PaymentAttempts.SingleAsync(a => a.Id == token.PaymentAttemptId);

            token.UsedAt = clock.UtcNow;

            if (intent.Status == PaymentStatus.RequiresAction
                && attempt.Status == AttemptStatus.ThreeDsInitiated)
            {
                attempt.MarkFailed(UnifiedErrors.ThreeDsTimeout, null,
                    "Müşteri 3D doğrulamadan dönmedi (zaman aşımı).");
                intent.MarkFailed();
                db.PaymentEvents.Add(PaymentEvent.For(intent, "payment.failed", "system",
                    new { attempt = attempt.PublicId, error = UnifiedErrors.ThreeDsTimeout }));
                db.OutboxMessages.Add(OutboxMessage.For(intent.TenantId,
                    KnownWebhookEvents.PaymentFailed, new
                    {
                        payment_id = intent.PublicId,
                        error = UnifiedErrors.ThreeDsTimeout,
                        amount_minor = intent.AmountMinor,
                        currency = intent.Currency,
                    }));
            }

            await db.SaveChangesAsync();
        }
    }
}
