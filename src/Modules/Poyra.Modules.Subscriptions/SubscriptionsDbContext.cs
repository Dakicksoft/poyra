using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Subscriptions.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Subscriptions;

public sealed class SubscriptionsDbContext(DbContextOptions<SubscriptionsDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_subscriptions";

    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionInvoice> SubscriptionInvoices => Set<SubscriptionInvoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SubscriptionsDbContext).Assembly);
    }

    public static SubscriptionsDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<SubscriptionsDbContext>(connectionString, MigrationsHistoryTable),
            TenantContext.Platform);
}

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PublicId).HasComputedColumnSql("'pln_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.Interval)
            .HasConversion(i => BillingIntervalMap.ToDb[i], i => BillingIntervalMap.FromDb[i])
            .HasMaxLength(10);
        b.HasIndex(x => x.TenantId);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_plans_amount_positive", "amount_minor > 0");
            t.HasCheckConstraint("ck_plans_interval_count", "interval_count BETWEEN 1 AND 24");
            t.HasCheckConstraint("ck_plans_trial_days", "trial_days BETWEEN 0 AND 365");
        });
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PublicId).HasComputedColumnSql("'sub_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();
        b.Property(x => x.CustomerRef).HasMaxLength(100);
        b.Property(x => x.CardToken).HasMaxLength(64);
        b.Property(x => x.Status)
            .HasConversion(s => SubscriptionStatusMap.ToDb[s], s => SubscriptionStatusMap.FromDb[s])
            .HasMaxLength(16);
        b.HasIndex(x => new { x.TenantId, x.CustomerRef });
        b.HasIndex(x => x.CurrentPeriodEnd).HasFilter("status IN ('active','trialing')");

        b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubscriptionInvoiceConfiguration : IEntityTypeConfiguration<SubscriptionInvoice>
{
    public void Configure(EntityTypeBuilder<SubscriptionInvoice> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PublicId).HasComputedColumnSql("'inv_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.LastPaymentId).HasMaxLength(64);
        b.Property(x => x.LastErrorCode).HasMaxLength(60);
        b.Property(x => x.Status)
            .HasConversion(s => InvoiceStatusMap.ToDb[s], s => InvoiceStatusMap.FromDb[s])
            .HasMaxLength(16);

        // Aynı dönem iki kez faturalanamaz (tahakkuk işi tekrar koşsa bile)
        b.HasIndex(x => new { x.SubscriptionId, x.PeriodStart }).IsUnique();
        b.HasIndex(x => x.NextRetryAt).HasFilter("status = 'retrying'");

        b.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SubscriptionsDbContextFactory : IDesignTimeDbContextFactory<SubscriptionsDbContext>
{
    public SubscriptionsDbContext CreateDbContext(string[] args)
        => SubscriptionsDbContext.CreateForMigrations(
            Environment.GetEnvironmentVariable("POYRA_MIGRATIONS_CS")
            ?? "Host=localhost;Port=5442;Database=poyra;Username=poyra;Password=poyra_pw");
}
