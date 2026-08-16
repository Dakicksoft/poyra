using Hangfire;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Webhooks.Contracts;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Payments.Infrastructure;

[AutomaticRetry(Attempts = 0)]
public sealed class OutboxDispatcher(
    PaymentsDbContext db, IWebhookFanout fanout, TenantContext tenant, IClock clock)
{
    public async Task DispatchPendingAsync()
    {
        var pending = await db.OutboxMessages
            .Where(m => m.DispatchedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(100)
            .ToListAsync();

        foreach (var message in pending)
        {
            tenant.Set(message.TenantId);
            message.FanoutCount = await fanout.FanOutAsync(message.EventType, message.PayloadJson, default);
            message.DispatchedAt = clock.UtcNow;
            await db.SaveChangesAsync(); // satır satır: kısmi ilerleme çökmede korunur
        }
    }
}

public interface IOutboxNudger
{
    void Nudge();
}

public sealed class HangfireOutboxNudger(IBackgroundJobClient jobs) : IOutboxNudger
{
    public void Nudge()
    {
        try
        {
            jobs.Enqueue<OutboxDispatcher>(d => d.DispatchPendingAsync());
        }
        catch
        {
            // en-iyi-çaba: kuyruk anlık erişilemezse dakikalık süpürme yakalar
        }
    }
}
