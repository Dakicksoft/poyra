using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Vault.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Vault;

public sealed class VaultDbContext(DbContextOptions<VaultDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_vault";

    public DbSet<CardToken> CardTokens => Set<CardToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VaultDbContext).Assembly);
    }

    public static VaultDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<VaultDbContext>(connectionString, MigrationsHistoryTable), TenantContext.Platform);
}

internal sealed class CardTokenConfiguration : IEntityTypeConfiguration<CardToken>
{
    public void Configure(EntityTypeBuilder<CardToken> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.PublicToken)
            .HasComputedColumnSql("'tok_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicToken).IsUnique();

        b.Property(x => x.CustomerRef).HasMaxLength(100);
        b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.Property(x => x.MaskedPan).HasMaxLength(25);
        b.Property(x => x.Brand).HasMaxLength(20);

        // Aynı kart aynı müşteri için iki kez saklanmaz (aktif kayıtlar arasında)
        b.HasIndex(x => new { x.TenantId, x.CustomerRef, x.Fingerprint })
            .HasFilter("deleted_at IS NULL")
            .IsUnique();
        b.HasIndex(x => new { x.TenantId, x.CustomerRef });

        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_card_tokens_expiry_month", "expiry_month BETWEEN 1 AND 12");
            t.HasCheckConstraint("ck_card_tokens_expiry_year", "expiry_year BETWEEN 2000 AND 2099");
        });
    }
}

public sealed class VaultDbContextFactory : IDesignTimeDbContextFactory<VaultDbContext>
{
    public VaultDbContext CreateDbContext(string[] args)
        => VaultDbContext.CreateForMigrations(
            Environment.GetEnvironmentVariable("POYRA_MIGRATIONS_CS")
            ?? "Host=localhost;Port=5442;Database=poyra;Username=poyra;Password=poyra_pw");
}
