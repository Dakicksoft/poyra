using Hangfire;
using Microsoft.EntityFrameworkCore;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Connectors.Domain;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.SharedKernel.Notifications;
using Poyra.SharedKernel.Security;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Connectors.Infrastructure;

/// <summary>
/// Konnektör sağlık otomasyonu: tüm aktif işyerlerinin aktif hesaplarını yoklar
/// (IPaymentConnector.ProbeAsync destekliyorsa) ve Health'i günceller. Rota motoru
/// Down hesapları zaten atlıyor — bu iş, kesintiyi İŞLEM DENEMEDEN önce yakalar.
/// Yoklama desteklemeyen konnektörlerin sağlığı elle/işlem sonuçlarından yönetilir.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class ConnectorCanaryJob(
    ConnectorsDbContext db,
    TenantContext tenant,
    ConnectorRegistry registry,
    ICredentialProtector protector,
    ITenantDirectory tenants,
    INotificationPublisher notifications)
{
    public async Task ProbeAllAsync()
    {
        foreach (var tenantId in await tenants.GetActiveTenantIdsAsync(default))
        {
            tenant.Set(tenantId); // RLS: bu işyerinin hesapları görünür olsun

            var accounts = await db.ConnectorAccounts
                .Where(a => a.Status == ConnectorAccountStatus.Active)
                .ToListAsync();

            var changed = new List<(string Label, ConnectorHealth Health)>();

            foreach (var account in accounts)
            {
                var probe = await ConnectorProbe.RunAsync(registry, protector, account, default);
                if (probe is null)
                    continue; // konnektör yoklama desteklemiyor — sağlığa dokunma

                var next = probe.Healthy ? ConnectorHealth.Healthy : ConnectorHealth.Down;
                if (account.Health == next)
                    continue;

                account.Health = next;
                changed.Add((account.Label, next));
            }

            await db.SaveChangesAsync();

            // Kesinti alarmı: panel banner'ı bu bildirimle anında yakalar
            foreach (var (label, health) in changed)
                await notifications.PublishAsync(new PoyraNotification(
                    tenantId, PoyraNotificationTypes.ConnectorHealthChanged, label,
                    health == ConnectorHealth.Healthy ? "healthy" : "down"));
        }
    }
}
