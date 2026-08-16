using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Ledger.Contracts;
using Poyra.Modules.Payments.Domain;

namespace Poyra.Modules.Payments.Infrastructure;

public sealed class CaptureFeed(PaymentsDbContext db) : ICaptureFeed
{
    public async Task<IReadOnlyList<CapturedCharge>> GetCapturedSinceAsync(
        DateTimeOffset sinceInclusive, int limit, CancellationToken ct)
        => await (
                from attempt in db.PaymentAttempts.AsNoTracking()
                join intent in db.PaymentIntents.AsNoTracking()
                    on attempt.PaymentIntentId equals intent.Id
                where attempt.Status == AttemptStatus.Captured
                      && attempt.CapturedAt != null && attempt.CapturedAt >= sinceInclusive
                orderby attempt.CapturedAt
                select new CapturedCharge(
                    attempt.PublicId,
                    intent.PublicId,
                    attempt.ConnectorAccountId,
                    attempt.AmountMinor,
                    intent.Currency,
                    attempt.Installments,
                    attempt.CapturedAt!.Value))
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CapturedCharge>> GetRefundedSinceAsync(
        DateTimeOffset sinceInclusive, int limit, CancellationToken ct)
        => await (
                from refund in db.Refunds.AsNoTracking()
                join intent in db.PaymentIntents.AsNoTracking()
                    on refund.PaymentIntentId equals intent.Id
                join attempt in db.PaymentAttempts.AsNoTracking()
                    on refund.PaymentAttemptId equals attempt.Id
                where refund.Status == RefundStatus.Succeeded
                      && refund.CreatedAt >= sinceInclusive
                orderby refund.CreatedAt
                select new CapturedCharge(
                    refund.PublicId,
                    intent.PublicId,
                    attempt.ConnectorAccountId,
                    refund.AmountMinor,
                    intent.Currency,
                    attempt.Installments,
                    refund.CreatedAt))
            .Take(limit)
            .ToListAsync(ct);
}
