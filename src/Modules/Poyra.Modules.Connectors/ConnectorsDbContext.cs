using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Connectors.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Connectors;

public sealed class ConnectorsDbContext(DbContextOptions<ConnectorsDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_connectors";

    public DbSet<ConnectorAccount> ConnectorAccounts => Set<ConnectorAccount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ConnectorsDbContext).Assembly);
    }

    public static ConnectorsDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<ConnectorsDbContext>(connectionString, MigrationsHistoryTable), TenantContext.Platform);
}

internal sealed class ConnectorAccountConfiguration : IEntityTypeConfiguration<ConnectorAccount>
{
    public void Configure(EntityTypeBuilder<ConnectorAccount> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ConnectorKey).HasMaxLength(40);
        b.Property(x => x.Label).HasMaxLength(100);
        b.Property(x => x.Status)
            .HasConversion(s => s.ToString().ToLowerInvariant(), s => Enum.Parse<ConnectorAccountStatus>(s, true))
            .HasMaxLength(16);
        b.Property(x => x.Health)
            .HasConversion(s => s.ToString().ToLowerInvariant(), s => Enum.Parse<Contracts.ConnectorHealth>(s, true))
            .HasMaxLength(16);
        b.HasIndex(x => new { x.TenantId, x.Label }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Priority });
    }
}

public sealed class ConnectorsDbContextFactory : IDesignTimeDbContextFactory<ConnectorsDbContext>
{
    public ConnectorsDbContext CreateDbContext(string[] args)
        => ConnectorsDbContext.CreateForMigrations(
            Environment.GetEnvironmentVariable("POYRA_MIGRATIONS_CS")
            ?? "Host=localhost;Port=5442;Database=poyra;Username=poyra;Password=poyra_pw");
}
