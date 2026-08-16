using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.PaymentLinks.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.PaymentLinks;

public sealed class PaymentLinksDbContext(DbContextOptions<PaymentLinksDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_payment_links";

    public DbSet<PaymentLink> PaymentLinks => Set<PaymentLink>();
    public DbSet<PaymentLinkLookup> PaymentLinkLookups => Set<PaymentLinkLookup>();
    public DbSet<PaymentLinkUsage> PaymentLinkUsages => Set<PaymentLinkUsage>();
    public DbSet<PaymentLinkAttempt> PaymentLinkAttempts => Set<PaymentLinkAttempt>();
    public DbSet<KarekodSettings> KarekodSettings => Set<KarekodSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentLinksDbContext).Assembly);
    }

    public static PaymentLinksDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<PaymentLinksDbContext>(connectionString, MigrationsHistoryTable),
            TenantContext.Platform);
}

internal sealed class PaymentLinkConfiguration : IEntityTypeConfiguration<PaymentLink>
{
    public void Configure(EntityTypeBuilder<PaymentLink> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PublicId).HasComputedColumnSql("'lnk_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();
        b.Property(x => x.Slug).HasMaxLength(32);
        b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.Status)
            .HasConversion(s => PaymentLinkStatusMap.ToDb[s], s => PaymentLinkStatusMap.FromDb[s])
            .HasMaxLength(16);
        b.HasIndex(x => new { x.TenantId, x.CreatedAt }).IsDescending(false, true);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_payment_links_amount", "amount_minor IS NULL OR amount_minor > 0");
            t.HasCheckConstraint("ck_payment_links_installments", "max_installments BETWEEN 1 AND 12");
            t.HasCheckConstraint("ck_payment_links_usage", "max_usage >= 0");
        });
    }
}

internal sealed class PaymentLinkLookupConfiguration : IEntityTypeConfiguration<PaymentLinkLookup>
{
    public void Configure(EntityTypeBuilder<PaymentLinkLookup> b)
    {
        b.HasKey(x => x.Slug);
        b.Property(x => x.Slug).HasMaxLength(32).ValueGeneratedNever();
        b.HasOne<PaymentLink>().WithMany()
            .HasForeignKey(x => x.PaymentLinkId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PaymentLinkUsageConfiguration : IEntityTypeConfiguration<PaymentLinkUsage>
{
    public void Configure(EntityTypeBuilder<PaymentLinkUsage> b)
    {
        // Anahtar ödeme kimliğidir: aynı ödeme iki kez sayılamaz (idempotent yazım)
        b.HasKey(x => x.PaymentPublicId);
        b.Property(x => x.PaymentPublicId).HasMaxLength(40).ValueGeneratedNever();
        b.HasOne<PaymentLink>().WithMany()
            .HasForeignKey(x => x.PaymentLinkId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PaymentLinkId, x.CreatedAt }).IsDescending(false, true);
    }
}

internal sealed class KarekodSettingsConfiguration : IEntityTypeConfiguration<KarekodSettings>
{
    public void Configure(EntityTypeBuilder<KarekodSettings> b)
    {
        b.HasKey(x => x.TenantId);
        b.Property(x => x.TenantId).ValueGeneratedNever();
        b.Property(x => x.SchemeGuid).HasMaxLength(32);
        b.Property(x => x.MerchantNo).HasMaxLength(32);
        b.Property(x => x.CategoryCode).HasMaxLength(4);
        b.Property(x => x.MerchantName).HasMaxLength(25); // EMVCo alan sınırı
        b.Property(x => x.MerchantCity).HasMaxLength(15);
    }
}

public sealed class PaymentLinksDbContextFactory : IDesignTimeDbContextFactory<PaymentLinksDbContext>
{
    public PaymentLinksDbContext CreateDbContext(string[] args)
        => PaymentLinksDbContext.CreateForMigrations(
            Environment.GetEnvironmentVariable("POYRA_MIGRATIONS_CS")
            ?? "Host=localhost;Port=5442;Database=poyra;Username=poyra;Password=poyra_pw");
}

internal sealed class PaymentLinkAttemptConfiguration : IEntityTypeConfiguration<PaymentLinkAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentLinkAttempt> b)
    {
        b.HasKey(x => x.PaymentPublicId);
        b.Property(x => x.PaymentPublicId).HasMaxLength(64);

        // Sonucu bekleyen denemeleri tarayan iş
        b.HasIndex(x => new { x.TenantId, x.PaymentLinkId });
    }
}
