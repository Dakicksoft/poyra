using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Compliance.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Compliance;

public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_compliance";

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<SuspiciousActivityReport> SuspiciousReports => Set<SuspiciousActivityReport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ComplianceDbContext).Assembly);
    }

    public static ComplianceDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<ComplianceDbContext>(connectionString, MigrationsHistoryTable),
            TenantContext.Platform);
}

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Actor).HasMaxLength(80);
        b.Property(x => x.Action).HasMaxLength(80);
        b.Property(x => x.ResourceType).HasMaxLength(60);
        b.Property(x => x.ResourceId).HasMaxLength(64);
        b.Property(x => x.Summary).HasMaxLength(500);
        b.Property(x => x.IpAddress).HasMaxLength(45);
        b.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");

        // Denetçinin üç sorusu: "ne zaman", "kim", "hangi kayıt"
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.Actor });
        b.HasIndex(x => new { x.ResourceType, x.ResourceId });
    }
}

internal sealed class SuspiciousActivityReportConfiguration
    : IEntityTypeConfiguration<SuspiciousActivityReport>
{
    public void Configure(EntityTypeBuilder<SuspiciousActivityReport> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PublicId).HasComputedColumnSql("'sar_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();
        b.Property(x => x.PaymentIds).HasMaxLength(2000);
        b.Property(x => x.CustomerRef).HasMaxLength(100);
        b.Property(x => x.Rationale).HasMaxLength(4000);
        b.Property(x => x.Resolution).HasMaxLength(4000);
        b.Property(x => x.Status)
            .HasConversion(s => SuspiciousReportStatusMap.ToDb[s], s => SuspiciousReportStatusMap.FromDb[s])
            .HasMaxLength(20);
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
    }
}
