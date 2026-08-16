using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Disputes.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Disputes;

public sealed class DisputesDbContext(DbContextOptions<DisputesDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_disputes";

    public DbSet<Dispute> Disputes => Set<Dispute>();
    public DbSet<DisputeEvidence> DisputeEvidences => Set<DisputeEvidence>();
    public DbSet<DisputeEvent> DisputeEvents => Set<DisputeEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DisputesDbContext).Assembly);
    }

    public static DisputesDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<DisputesDbContext>(connectionString, MigrationsHistoryTable),
            TenantContext.Platform);
}

internal sealed class DisputeConfiguration : IEntityTypeConfiguration<Dispute>
{
    public void Configure(EntityTypeBuilder<Dispute> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PublicId).HasComputedColumnSql("'dsp_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();

        b.Property(x => x.PaymentPublicId).HasMaxLength(64);
        b.Property(x => x.ConnectorDisputeId).HasMaxLength(100);
        b.Property(x => x.ConnectorKey).HasMaxLength(40);
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.Reason).HasMaxLength(64);
        b.Property(x => x.RawReasonCode).HasMaxLength(40);
        b.Property(x => x.EvidenceSummary).HasMaxLength(4000);

        b.Property(x => x.Stage)
            .HasConversion(s => DisputeStageMap.ToDb[s], s => DisputeStageMap.FromDb[s])
            .HasMaxLength(20);
        b.Property(x => x.Status)
            .HasConversion(s => DisputeStatusMap.ToDb[s], s => DisputeStatusMap.FromDb[s])
            .HasMaxLength(20);

        b.HasIndex(x => x.TenantId);
        b.HasIndex(x => x.PaymentIntentId);

        // Süre taraması: yalnız açık dosyalar
        b.HasIndex(x => x.EvidenceDueAt).HasFilter("status = 'open'");

        // Aynı banka dosyası iki kez kaydedilmesin — bildirim tekrarı gelebilir
        b.HasIndex(x => new { x.TenantId, x.ConnectorDisputeId })
            .IsUnique()
            .HasFilter("connector_dispute_id IS NOT NULL");
    }
}

internal sealed class DisputeEvidenceConfiguration : IEntityTypeConfiguration<DisputeEvidence>
{
    public void Configure(EntityTypeBuilder<DisputeEvidence> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.FileName).HasMaxLength(255);
        b.Property(x => x.ContentType).HasMaxLength(100);
        b.Property(x => x.Kind).HasMaxLength(40);
        b.Property(x => x.Content).HasColumnType("bytea");
        b.HasIndex(x => x.DisputeId);

        b.HasOne<Dispute>().WithMany()
            .HasForeignKey(x => x.DisputeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DisputeEventConfiguration : IEntityTypeConfiguration<DisputeEvent>
{
    public void Configure(EntityTypeBuilder<DisputeEvent> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.EventType).HasMaxLength(64);
        b.Property(x => x.Actor).HasMaxLength(100);
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.HasIndex(x => x.DisputeId);

        b.HasOne<Dispute>().WithMany()
            .HasForeignKey(x => x.DisputeId).OnDelete(DeleteBehavior.Restrict);
    }
}
