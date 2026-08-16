using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Payments.Domain;

namespace Poyra.Modules.Payments.Infrastructure;

public sealed class PaymentVelocitySource(PaymentsDbContext db) : IPaymentVelocitySource
{
    public async Task<VelocitySnapshot> GetAsync(
        string? customerRef, string? ipAddress, string? maskedPan, DateTimeOffset now, CancellationToken ct)
    {
        if (customerRef is null && ipAddress is null && maskedPan is null)
            return new VelocitySnapshot(0, 0, 0, 0, 0);

        var since24h = now.AddHours(-24);
        var since1h = now.AddHours(-1);

        var attempts = await (
            from attempt in db.PaymentAttempts.AsNoTracking()
            join intent in db.PaymentIntents.AsNoTracking() on attempt.PaymentIntentId equals intent.Id
            where attempt.CreatedAt >= since24h
                  && ((customerRef != null && intent.CustomerRef == customerRef)
                      || (ipAddress != null && intent.CustomerIp == ipAddress)
                      || (maskedPan != null && attempt.MaskedPan == maskedPan))
            select new
            {
                attempt.CreatedAt,
                attempt.Status,
                attempt.AmountMinor,
                attempt.MaskedPan,
            }).ToListAsync(ct);

        return new VelocitySnapshot(
            Attempts1h: attempts.Count(a => a.CreatedAt >= since1h),
            Attempts24h: attempts.Count,
            Declines1h: attempts.Count(a => a.CreatedAt >= since1h && a.Status == AttemptStatus.Failed),
            Amount24hMinor: attempts.Sum(a => a.AmountMinor),
            DistinctCards24h: attempts.Where(a => a.MaskedPan != null)
                .Select(a => a.MaskedPan!).Distinct().Count());
    }
}

public sealed class PermissiveRiskGate : IRiskGate
{
    public Task<RiskDecision> AssessAsync(RiskContext context, CancellationToken ct)
        => Task.FromResult(RiskDecision.Allowed);
}
