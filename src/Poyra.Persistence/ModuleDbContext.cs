using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Persistence;

public abstract class ModuleDbContext(DbContextOptions options, TenantContext tenant) : DbContext(options)
{
    private static readonly MethodInfo ApplyFilterMethod = typeof(ModuleDbContext)
        .GetMethod(nameof(ApplyTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    protected TenantContext Tenant { get; } = tenant;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var tenantOwnedTypes = modelBuilder.Model
            .GetEntityTypes()
            .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType))
            .Select(t => t.ClrType)
            .ToList();

        foreach (var clrType in tenantOwnedTypes)
            ApplyFilterMethod.MakeGenericMethod(clrType).Invoke(this, [modelBuilder]);
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantOwned
        => modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == Tenant.TenantId);
}
