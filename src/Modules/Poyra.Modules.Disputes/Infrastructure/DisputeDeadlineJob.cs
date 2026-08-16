using Hangfire;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Disputes.Contracts;
using Poyra.Modules.Disputes.Domain;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Disputes.Infrastructure;

/// <summary>
/// Süre bekçisi.
///
/// Bu modülün varlık sebebi budur: kaçırılan kanıt süresi — savunma ne kadar güçlü
/// olursa olsun — otomatik kayıptır. İşyerinin panele bakmasını beklemek, kaybı
/// işyerinin dikkatine havale etmektir.
///
/// İki iş yapar:
///  1- Süresi yaklaşan açık dosyalar için bir kez uyarı yayınlar
///  2- Süresi geçmiş açık dosyaları 'expired' olarak kapatır — sessizce "açık"
///    kalması, kaybedilmiş bir dosyayı hâlâ savunulabilir göstermek olurdu
/// </summary>
[AutomaticRetry(Attempts = 2)]
public sealed class DisputeDeadlineJob(
    DisputesDbContext db,
    TenantContext tenant,
    ITenantDirectory tenants,
    IDisputeNotifier notifier,
    IClock clock)
{
    public async Task SweepAsync()
    {
        var now = clock.UtcNow;
        var warnAt = now + DisputeDeadline.WarningWindow;

        foreach (var tenantId in await tenants.GetActiveTenantIdsAsync(default))
        {
            tenant.Set(tenantId); // RLS: bundan sonraki her bağlantı bu işyerine kilitli

            var open = await db.Disputes
                .Where(d => d.Status == DisputeStatus.Open && d.EvidenceDueAt <= warnAt)
                .ToListAsync();

            foreach (var dispute in open)
            {
                if (dispute.IsOverdue(now))
                {
                    dispute.MarkExpired(now);
                    db.DisputeEvents.Add(DisputeEvent.For(
                        dispute, KnownDisputeEvents.Expired, "system",
                        new { evidence_due_at = dispute.EvidenceDueAt }));

                    await db.SaveChangesAsync();
                    await notifier.PublishAsync(dispute, KnownDisputeEvents.Expired, new
                    {
                        amount_minor = dispute.AmountMinor,
                        reason = "evidence_window_closed",
                    }, default);
                    continue;
                }

                if (dispute.DueWarningSentAt is not null)
                    continue;

                dispute.DueWarningSentAt = now;
                db.DisputeEvents.Add(DisputeEvent.For(
                    dispute, KnownDisputeEvents.EvidenceDueSoon, "system",
                    new { evidence_due_at = dispute.EvidenceDueAt }));

                await db.SaveChangesAsync();
                await notifier.PublishAsync(dispute, KnownDisputeEvents.EvidenceDueSoon, new
                {
                    amount_minor = dispute.AmountMinor,
                    remaining_hours = Math.Round(dispute.Remaining(now).TotalHours, 1),
                }, default);
            }
        }
    }
}
