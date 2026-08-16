using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Field.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Field;

public sealed class FieldDbContext(DbContextOptions<FieldDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_field";

    public DbSet<FieldAgent> FieldAgents => Set<FieldAgent>();
    public DbSet<FieldCollection> FieldCollections => Set<FieldCollection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FieldDbContext).Assembly);
    }

    public static FieldDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<FieldDbContext>(connectionString, MigrationsHistoryTable),
            TenantContext.Platform);
}

internal sealed class FieldAgentConfiguration : IEntityTypeConfiguration<FieldAgent>
{
    public void Configure(EntityTypeBuilder<FieldAgent> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).HasMaxLength(60);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Region).HasMaxLength(100);
        b.Property(x => x.DeviceId).HasMaxLength(128);
        b.Property(x => x.PinHash).HasMaxLength(200);
        b.Property(x => x.DisabledReason).HasMaxLength(500);

        b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();

        b.HasIndex(x => new { x.TenantId, x.DeviceId })
            .IsUnique()
            .HasFilter("device_id IS NOT NULL AND disabled_at IS NULL");
    }
}

internal sealed class FieldCollectionConfiguration : IEntityTypeConfiguration<FieldCollection>
{
    public void Configure(EntityTypeBuilder<FieldCollection> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PublicId).HasComputedColumnSql("'fc_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();

        b.Property(x => x.CustomerRef).HasMaxLength(100);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.PaymentLinkId).HasMaxLength(64);
        b.Property(x => x.PaymentId).HasMaxLength(64);
        b.Property(x => x.CheckoutUrl).HasMaxLength(500);
        b.Property(x => x.Note).HasMaxLength(1000);
        b.Property(x => x.DeviceClaims).HasColumnType("jsonb");

        b.Property(x => x.Method)
            .HasConversion(m => FieldCollectionMethodMap.ToDb[m], m => FieldCollectionMethodMap.FromDb[m])
            .HasMaxLength(20);

        b.Property(x => x.Status)
            .HasConversion(s => FieldCollectionStatusMap.ToDb[s], s => FieldCollectionStatusMap.FromDb[s])
            .HasMaxLength(20);

        // ÇEVRİMDIŞI KUYRUĞUN GÜVENLİK AĞI. Ağ, sunucu kaydettikten sonra ama onay
        // dönmeden koparsa cihaz aynı kaydı yeniden gönderir. Bu kısıt olmasaydı
        // müşteriden iki kez tahsilat istenirdi.
        b.HasIndex(x => new { x.TenantId, x.ClientOpId }).IsUnique();

        // Gün sonu özeti: temsilci × sunucu zamanı
        b.HasIndex(x => new { x.TenantId, x.AgentId, x.OccurredAtServer });

        // Sonucu beklenen kayıtları tarayan iş
        b.HasIndex(x => new { x.TenantId, x.Status });
    }
}

public sealed class FieldDbContextFactory : IDesignTimeDbContextFactory<FieldDbContext>
{
    public FieldDbContext CreateDbContext(string[] args)
        => FieldDbContext.CreateForMigrations(
            Environment.GetEnvironmentVariable("POYRA_MIGRATIONS_CS")
            ?? "Host=localhost;Port=5442;Database=poyra;Username=poyra;Password=poyra_pw");
}
