using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Payments.Domain;

namespace Poyra.Modules.Payments.Infrastructure;

public sealed class PaymentLedger(PaymentsDbContext db) : IPaymentLedger
{
    private static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);

    public async Task<LedgerAttempt?> FindCapturedByOrderIdAsync(string orderId, CancellationToken ct)
        => await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.PublicId == orderId && a.Status == AttemptStatus.Captured)
            .Select(a => new LedgerAttempt(
                a.Id, a.PublicId, a.ConnectorAccountId, a.AmountMinor, a.Installments, a.CapturedAt,
                a.CardBank))
            .SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<LedgerAttempt>> GetCapturedForDayAsync(
        Guid connectorAccountId, DateOnly dayTr, CancellationToken ct)
    {
        var start = new DateTimeOffset(dayTr.ToDateTime(TimeOnly.MinValue), TurkeyOffset).ToUniversalTime();
        var end = start.AddDays(1);

        return await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.ConnectorAccountId == connectorAccountId
                        && a.Status == AttemptStatus.Captured
                        && a.CapturedAt >= start && a.CapturedAt < end)
            .OrderBy(a => a.CapturedAt)
            .Select(a => new LedgerAttempt(
                a.Id, a.PublicId, a.ConnectorAccountId, a.AmountMinor, a.Installments, a.CapturedAt,
                a.CardBank))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LedgerRefund>> GetSucceededRefundsByAttemptOrderIdAsync(
        string orderId, CancellationToken ct)
        => await (
                from attempt in db.PaymentAttempts.AsNoTracking()
                join refund in db.Refunds.AsNoTracking() on attempt.Id equals refund.PaymentAttemptId
                where attempt.PublicId == orderId && refund.Status == RefundStatus.Succeeded
                orderby refund.CreatedAt
                select new LedgerRefund(refund.Id, refund.PublicId, refund.AmountMinor))
            .ToListAsync(ct);
}

public sealed class PaymentLookup(PaymentsDbContext db) : IPaymentLookup
{
    public async Task<PaymentSummary?> FindByPublicIdAsync(string publicId, CancellationToken ct)
        => await db.PaymentIntents.AsNoTracking()
            .Where(i => i.PublicId == publicId)
            .Select(i => new PaymentSummary(
                i.Id, i.PublicId, i.AmountMinor, i.Currency,
                db.PaymentAttempts.Any(a => a.PaymentIntentId == i.Id && a.Status == AttemptStatus.Captured)))
            .SingleOrDefaultAsync(ct);
}

public sealed class CustomerPaymentSource(PaymentsDbContext db) : ICustomerPaymentSource
{
    public async Task<IReadOnlyList<CustomerPayment>> GetPaymentsAsync(
        string customerRef, int limit, CancellationToken ct)
    {
        var rows = await (
            from intent in db.PaymentIntents.AsNoTracking()
            where intent.CustomerRef == customerRef
            orderby intent.CreatedAt descending
            select new
            {
                intent.PublicId,
                intent.AmountMinor,
                intent.Currency,
                intent.Status,
                intent.Installments,
                MaskedPan = db.PaymentAttempts
                    .Where(a => a.PaymentIntentId == intent.Id && a.MaskedPan != null)
                    .OrderByDescending(a => a.CreatedAt)
                    .Select(a => a.MaskedPan)
                    .FirstOrDefault(),
                intent.CreatedAt,
            })
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);

        return rows.Select(r => new CustomerPayment(
            r.PublicId, r.AmountMinor, r.Currency, PaymentStatusMap.ToDb[r.Status],
            r.Installments, r.MaskedPan, r.CreatedAt)).ToList();
    }

    public async Task<CustomerPaymentTotals> GetTotalsAsync(string customerRef, CancellationToken ct)
    {
        var intents = await db.PaymentIntents.AsNoTracking()
            .Where(i => i.CustomerRef == customerRef)
            .Select(i => new { i.Id, i.Status, i.AmountMinor })
            .ToListAsync(ct);

        if (intents.Count == 0)
            return new CustomerPaymentTotals(0, 0, 0);

        var ids = intents.Select(i => i.Id).ToList();

        var captured = await db.PaymentAttempts.AsNoTracking()
            .Where(a => ids.Contains(a.PaymentIntentId) && a.Status == AttemptStatus.Captured)
            .SumAsync(a => (long?)a.AmountMinor, ct) ?? 0;

        var refunded = await db.Refunds.AsNoTracking()
            .Where(r => ids.Contains(r.PaymentIntentId) && r.Status == RefundStatus.Succeeded)
            .SumAsync(r => (long?)r.AmountMinor, ct) ?? 0;

        return new CustomerPaymentTotals(intents.Count, captured, refunded);
    }
}
