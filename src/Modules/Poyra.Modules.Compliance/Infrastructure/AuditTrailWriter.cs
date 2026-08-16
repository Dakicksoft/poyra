using System.Text.Json;
using Microsoft.Extensions.Logging;
using Poyra.Modules.Compliance.Domain;
using Poyra.SharedKernel.Audit;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Compliance.Infrastructure;


public sealed class AuditTrailWriter(
    ComplianceDbContext db, TenantContext tenant, ILogger<AuditTrailWriter> logger) : IAuditTrail
{
    public async Task RecordAsync(AuditEvent entry, CancellationToken ct)
    {
        if (!tenant.HasTenant)
            return;

        try
        {
            db.AuditLog.Add(new AuditLogEntry
            {
                TenantId = tenant.TenantId,
                Actor = entry.Actor,
                Action = entry.Action,
                ResourceType = entry.ResourceType,
                ResourceId = entry.ResourceId,
                Summary = entry.Summary,
                IpAddress = entry.IpAddress,
                Metadata = entry.Metadata is null ? "{}" : JsonSerializer.Serialize(entry.Metadata),
            });

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Denetim kaydı yazılamadı: {Action} / {Resource}",
                entry.Action, entry.ResourceType);
        }
    }
}
