using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.Modules.Tenancy.Domain;

namespace Poyra.Modules.Tenancy.Infrastructure;

public sealed class TenantDirectory(TenancyDbContext db) : ITenantDirectory
{
    public async Task<IReadOnlyList<Guid>> GetActiveTenantIdsAsync(CancellationToken ct)
        => await db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.Id)
            .ToListAsync(ct);
}
