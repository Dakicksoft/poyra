using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Routing.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Routing;

public sealed class RoutingDbContext(DbContextOptions<RoutingDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_routing";

    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();
    public DbSet<VolumeCommitment> VolumeCommitments => Set<VolumeCommitment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RoutingDbContext).Assembly);
    }

    public static RoutingDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<RoutingDbContext>(connectionString, MigrationsHistoryTable), TenantContext.Platform);
}

internal sealed class RoutingRuleConfiguration : IEntityTypeConfiguration<RoutingRule>
{
    public void Configure(EntityTypeBuilder<RoutingRule> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Document).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.Name, x.Version }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.IsActive })
            .HasFilter("is_active = true")
            .IsUnique();
    }
}

internal sealed class VolumeCommitmentConfiguration : IEntityTypeConfiguration<VolumeCommitment>
{
    public void Configure(EntityTypeBuilder<VolumeCommitment> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasIndex(x => new { x.TenantId, x.ConnectorAccountId }).IsUnique();

        b.ToTable(t => t.HasCheckConstraint(
            "ck_volume_commitments_target", "monthly_target_minor > 0"));
    }
}

public sealed class RoutingDbContextFactory : IDesignTimeDbContextFactory<RoutingDbContext>
{
    public RoutingDbContext CreateDbContext(string[] args)
        => RoutingDbContext.CreateForMigrations(
            Environment.GetEnvironmentVariable("POYRA_MIGRATIONS_CS")
            ?? "Host=localhost;Port=5442;Database=poyra;Username=poyra;Password=poyra_pw");
}
