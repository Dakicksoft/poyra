using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Recon.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Recon;

public sealed class ReconDbContext(DbContextOptions<ReconDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_recon";

    public DbSet<CommissionAgreement> CommissionAgreements => Set<CommissionAgreement>();
    public DbSet<ReconStatement> ReconStatements => Set<ReconStatement>();
    public DbSet<ReconStatementLine> ReconStatementLines => Set<ReconStatementLine>();
    public DbSet<CommissionAuditFinding> CommissionAuditFindings => Set<CommissionAuditFinding>();
    public DbSet<CommissionClaim> CommissionClaims => Set<CommissionClaim>();
    public DbSet<CommissionClaimEvent> CommissionClaimEvents => Set<CommissionClaimEvent>();
    public DbSet<BankHoliday> BankHolidays => Set<BankHoliday>();
    public DbSet<ErpExportSettings> ErpExportSettings => Set<ErpExportSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReconDbContext).Assembly);
    }

    public static ReconDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<ReconDbContext>(connectionString, MigrationsHistoryTable), TenantContext.Platform);
}

internal sealed class CommissionAgreementConfiguration : IEntityTypeConfiguration<CommissionAgreement>
{
    public void Configure(EntityTypeBuilder<CommissionAgreement> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasIndex(x => new { x.TenantId, x.ConnectorAccountId, x.InstallmentCount }).IsUnique();
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_commission_agreements_count", "installment_count BETWEEN 1 AND 12");
            t.HasCheckConstraint("ck_commission_agreements_rate", "rate_bps BETWEEN 0 AND 10000");
        });
    }
}

internal sealed class ReconStatementConfiguration : IEntityTypeConfiguration<ReconStatement>
{
    public void Configure(EntityTypeBuilder<ReconStatement> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Status)
            .HasConversion(s => s.ToString().ToLowerInvariant(), s => Enum.Parse<StatementStatus>(s, true))
            .HasMaxLength(16);
        b.HasIndex(x => new { x.TenantId, x.ConnectorAccountId, x.StatementDate });
        b.HasIndex(x => x.Status).HasFilter("status = 'matching'");
    }
}

internal sealed class ReconStatementLineConfiguration : IEntityTypeConfiguration<ReconStatementLine>
{
    public void Configure(EntityTypeBuilder<ReconStatementLine> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.OrderId).HasMaxLength(64);
        b.Property(x => x.MatchStatus)
            .HasConversion(
                s => s == LineMatchStatus.Pending ? "pending"
                    : s == LineMatchStatus.Matched ? "matched"
                    : s == LineMatchStatus.MissingInPoyra ? "missing_in_poyra" : "amount_mismatch",
                s => s == "pending" ? LineMatchStatus.Pending
                    : s == "matched" ? LineMatchStatus.Matched
                    : s == "missing_in_poyra" ? LineMatchStatus.MissingInPoyra : LineMatchStatus.AmountMismatch)
            .HasMaxLength(20);
        b.Property(x => x.LineType)
            .HasConversion(
                t => t == StatementLineType.Sale ? "sale" : "refund",
                t => t == "sale" ? StatementLineType.Sale : StatementLineType.Refund)
            .HasMaxLength(10);
        b.HasIndex(x => new { x.StatementId, x.LineNo }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.OrderId });

        b.HasOne<ReconStatement>().WithMany()
            .HasForeignKey(x => x.StatementId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CommissionAuditFindingConfiguration : IEntityTypeConfiguration<CommissionAuditFinding>
{
    public void Configure(EntityTypeBuilder<CommissionAuditFinding> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasIndex(x => new { x.TenantId, x.StatementId });
        b.HasOne<ReconStatement>().WithMany()
            .HasForeignKey(x => x.StatementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReconStatementLine>().WithMany()
            .HasForeignKey(x => x.StatementLineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ErpExportSettingsConfiguration : IEntityTypeConfiguration<ErpExportSettings>
{
    public void Configure(EntityTypeBuilder<ErpExportSettings> b)
    {
        b.HasKey(x => x.TenantId);
        b.Property(x => x.TenantId).ValueGeneratedNever();
        b.Property(x => x.Format)
            .HasConversion(f => ErpFormatMap.ToDb[f], f => ErpFormatMap.FromDb[f])
            .HasMaxLength(16);
        b.Property(x => x.PosReceivableAccount).HasMaxLength(32);
        b.Property(x => x.BankAccount).HasMaxLength(32);
        b.Property(x => x.CommissionExpenseAccount).HasMaxLength(32);
        b.Property(x => x.DocumentPrefix).HasMaxLength(10);
    }
}

internal sealed class BankHolidayConfiguration : IEntityTypeConfiguration<BankHoliday>
{
    public void Configure(EntityTypeBuilder<BankHoliday> b)
    {
        b.HasKey(x => x.Day);
        b.Property(x => x.Day).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(100);
    }
}

internal sealed class CommissionClaimConfiguration : IEntityTypeConfiguration<CommissionClaim>
{
    public void Configure(EntityTypeBuilder<CommissionClaim> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PublicId).HasComputedColumnSql("'clm_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();
        b.Property(x => x.BankReference).HasMaxLength(120);
        b.Property(x => x.Status)
            .HasConversion(s => ClaimStatusMap.ToDb[s], s => ClaimStatusMap.FromDb[s])
            .HasMaxLength(30);
        b.Ignore(x => x.OutstandingMinor);
        b.HasIndex(x => new { x.TenantId, x.Status });
    }
}

internal sealed class CommissionClaimEventConfiguration : IEntityTypeConfiguration<CommissionClaimEvent>
{
    public void Configure(EntityTypeBuilder<CommissionClaimEvent> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.EventType).HasMaxLength(30);
        b.Property(x => x.Note).HasMaxLength(2000);

        b.HasOne<CommissionClaim>().WithMany().HasForeignKey(x => x.ClaimId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.ClaimId, x.CreatedAt });
    }
}

public sealed class ReconDbContextFactory : IDesignTimeDbContextFactory<ReconDbContext>
{
    public ReconDbContext CreateDbContext(string[] args)
        => ReconDbContext.CreateForMigrations(
            Environment.GetEnvironmentVariable("POYRA_MIGRATIONS_CS")
            ?? "Host=localhost;Port=5442;Database=poyra;Username=poyra;Password=poyra_pw");
}
