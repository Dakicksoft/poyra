using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Risk.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Risk;

public sealed class RiskDbContext(DbContextOptions<RiskDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_risk";

    public DbSet<RiskRuleSet> RiskRuleSets => Set<RiskRuleSet>();
    public DbSet<BlocklistEntry> BlocklistEntries => Set<BlocklistEntry>();
    public DbSet<RiskAssessment> RiskAssessments => Set<RiskAssessment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RiskDbContext).Assembly);
    }

    public static RiskDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<RiskDbContext>(connectionString, MigrationsHistoryTable),
            TenantContext.Platform);
}

internal sealed class RiskRuleSetConfiguration : IEntityTypeConfiguration<RiskRuleSet>
{
    public void Configure(EntityTypeBuilder<RiskRuleSet> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Document).HasColumnName("document").HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.Version }).IsUnique();

        // İşyeri başına EN ÇOK BİR aktif set — iki aktif set, hangisinin geçerli
        // olduğunu belirsiz bırakır ve engelleme kararını rastgeleleştirir.
        b.HasIndex(x => x.TenantId).IsUnique().HasFilter("active");
    }
}

internal sealed class BlocklistEntryConfiguration : IEntityTypeConfiguration<BlocklistEntry>
{
    public void Configure(EntityTypeBuilder<BlocklistEntry> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Value).HasMaxLength(255);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Kind)
            .HasConversion(k => BlocklistKindMap.ToDb[k], k => BlocklistKindMap.FromDb[k])
            .HasMaxLength(20);

        // Aynı değer iki kez AKTİF eklenemez; kaldırılmış kayıt yeniden eklenebilir
        b.HasIndex(x => new { x.TenantId, x.Kind, x.Value })
            .IsUnique()
            .HasFilter("removed_at IS NULL");
    }
}

internal sealed class RiskAssessmentConfiguration : IEntityTypeConfiguration<RiskAssessment>
{
    public void Configure(EntityTypeBuilder<RiskAssessment> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PaymentId).HasMaxLength(64);
        b.Property(x => x.Outcome).HasMaxLength(20);
        b.Property(x => x.RuleName).HasMaxLength(120);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Flow).HasMaxLength(10);
        b.Property(x => x.Signals).HasColumnName("signals").HasColumnType("jsonb");
        b.HasIndex(x => x.PaymentId);
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
    }
}
