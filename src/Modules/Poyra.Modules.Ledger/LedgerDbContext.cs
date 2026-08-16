using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Ledger.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Ledger;

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_ledger";

    public DbSet<BankReceivable> BankReceivables => Set<BankReceivable>();
    public DbSet<SettlementStatement> SettlementStatements => Set<SettlementStatement>();
    public DbSet<SettlementLine> SettlementLines => Set<SettlementLine>();
    public DbSet<SettlementFinding> SettlementFindings => Set<SettlementFinding>();
    public DbSet<LedgerSettings> LedgerSettings => Set<LedgerSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);
    }

    public static LedgerDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<LedgerDbContext>(connectionString, MigrationsHistoryTable),
            TenantContext.Platform);
}

internal sealed class BankReceivableConfiguration : IEntityTypeConfiguration<BankReceivable>
{
    public void Configure(EntityTypeBuilder<BankReceivable> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PublicId).HasComputedColumnSql("'rcv_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();

        b.Property(x => x.AttemptPublicId).HasMaxLength(64);
        b.Property(x => x.PaymentPublicId).HasMaxLength(64);
        b.Property(x => x.Currency).HasMaxLength(3);

        b.Property(x => x.Kind)
            .HasConversion(k => ReceivableKindMap.ToDb[k], k => ReceivableKindMap.FromDb[k])
            .HasMaxLength(20);

        b.Property(x => x.Status)
            .HasConversion(s => ReceivableStatusMap.ToDb[s], s => ReceivableStatusMap.FromDb[s])
            .HasMaxLength(20);

        // Türetilmiş özellikler kolon DEĞİLDİR — hesaplama tek yerde kalsın
        b.Ignore(x => x.CommissionDeltaMinor);

        // ALACAK İKİ KEZ YAZILAMAZ. Projeksiyon işi yeniden koşabilir (deploy, çökme,
        // pencere kayması); tekillik olmasaydı işyerine olmayan para borç görünürdü.
        b.HasIndex(x => new { x.TenantId, x.AttemptPublicId, x.Kind }).IsUnique();

        // "Bugün gelmesi gereken" ve "geciken" sorguları
        b.HasIndex(x => new { x.TenantId, x.Status, x.ExpectedValueDate });

        // Banka bazlı özet
        b.HasIndex(x => new { x.TenantId, x.ConnectorAccountId });
    }
}

internal sealed class SettlementStatementConfiguration : IEntityTypeConfiguration<SettlementStatement>
{
    public void Configure(EntityTypeBuilder<SettlementStatement> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PublicId).HasComputedColumnSql("'set_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();
        b.Property(x => x.Format).HasMaxLength(20);
        b.Property(x => x.FileName).HasMaxLength(255);
        b.HasIndex(x => new { x.TenantId, x.ConnectorAccountId });
    }
}

internal sealed class SettlementLineConfiguration : IEntityTypeConfiguration<SettlementLine>
{
    public void Configure(EntityTypeBuilder<SettlementLine> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Reference).HasMaxLength(120);

        // İlişki EF'e TANITILIR (ham SQL'de bırakılmaz): yoksa EF ekleme sırasını
        // bilemez ve satırları ekstreden ÖNCE yazıp yabancı anahtarı ihlal eder.
        b.HasOne<SettlementStatement>()
            .WithMany()
            .HasForeignKey(x => x.StatementId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.StatementId, x.ValueDate });
    }
}

internal sealed class SettlementFindingConfiguration : IEntityTypeConfiguration<SettlementFinding>
{
    public void Configure(EntityTypeBuilder<SettlementFinding> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Outcome)
            .HasConversion(o => SettlementOutcomeMap.ToDb[o], o => SettlementOutcomeMap.FromDb[o])
            .HasMaxLength(20);

        b.HasOne<SettlementStatement>()
            .WithMany()
            .HasForeignKey(x => x.StatementId)
            .OnDelete(DeleteBehavior.Restrict);

        // Aynı ekstre + aynı gün için tek bulgu: yeniden yükleme çift kayıt açmamalı
        b.HasIndex(x => new { x.TenantId, x.StatementId, x.ValueDate }).IsUnique();

        // "Eksik yatanlar" raporu
        b.HasIndex(x => new { x.TenantId, x.Outcome, x.ValueDate });
    }
}

internal sealed class LedgerSettingsConfiguration : IEntityTypeConfiguration<LedgerSettings>
{
    public void Configure(EntityTypeBuilder<LedgerSettings> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        // İşyeri başına TEK ayar satırı
        b.HasIndex(x => x.TenantId).IsUnique();
    }
}

public sealed class LedgerDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
        => LedgerDbContext.CreateForMigrations(
            Environment.GetEnvironmentVariable("POYRA_MIGRATIONS_CS")
            ?? "Host=localhost;Port=5442;Database=poyra;Username=poyra;Password=poyra_pw");
}
