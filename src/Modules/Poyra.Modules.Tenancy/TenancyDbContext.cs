using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Tenancy.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Tenancy;

public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_tenancy";

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<BusinessProfile> BusinessProfiles => Set<BusinessProfile>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserTenant> UserTenants => Set<UserTenant>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserToken> UserTokens => Set<UserToken>();
    public DbSet<EmailMessageRecord> EmailMessages => Set<EmailMessageRecord>();
    public DbSet<SmsMessageRecord> SmsMessages => Set<SmsMessageRecord>();
    public DbSet<TenantBranding> TenantBrandings => Set<TenantBranding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TenancyDbContext).Assembly);
    }

    public static TenancyDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<TenancyDbContext>(connectionString, MigrationsHistoryTable), TenantContext.Platform);
}
