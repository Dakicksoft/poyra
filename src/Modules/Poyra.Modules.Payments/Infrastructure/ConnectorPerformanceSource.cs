using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Routing.Contracts;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Payments.Infrastructure;

public sealed class ConnectorPerformanceSource(PaymentsDbContext db, IClock clock) : IConnectorPerformanceSource
{
    public async Task<IReadOnlyList<ConnectorPerformance>> GetAsync(TimeSpan window, CancellationToken ct)
    {
        var since = clock.UtcNow - window;

        var rows = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.CreatedAt >= since
                        && (a.Status == AttemptStatus.Captured || a.Status == AttemptStatus.Failed))
            .Select(a => new
            {
                a.ConnectorAccountId,
                Succeeded = a.Status == AttemptStatus.Captured,
                a.LatencyMs,
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.ConnectorAccountId)
            .Select(group =>
            {
                var latencies = group.Where(r => r.LatencyMs.HasValue)
                    .Select(r => r.LatencyMs!.Value)
                    .OrderBy(ms => ms)
                    .ToList();

                return new ConnectorPerformance(
                    group.Key,
                    (double)group.Count(r => r.Succeeded) / group.Count(),
                    latencies.Count == 0 ? 0 : latencies[latencies.Count / 2], // p50
                    group.Count());
            })
            .ToList();
    }
}
