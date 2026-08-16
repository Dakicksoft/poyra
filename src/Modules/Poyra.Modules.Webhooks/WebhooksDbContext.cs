using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Webhooks.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Webhooks;

public sealed class WebhooksDbContext(DbContextOptions<WebhooksDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_webhooks";

    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WebhooksDbContext).Assembly);
    }

    public static WebhooksDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<WebhooksDbContext>(connectionString, MigrationsHistoryTable), TenantContext.Platform);
}

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Url).HasMaxLength(2000);
        b.Property(x => x.EventTypes).HasColumnType("text[]");
        b.HasIndex(x => x.TenantId);
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.EventType).HasMaxLength(64);
        b.Property(x => x.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        b.Property(x => x.LastError).HasMaxLength(500);
        b.Property(x => x.Status)
            .HasConversion(s => DeliveryStatusMap.ToDb[s], s => DeliveryStatusMap.FromDb[s])
            .HasMaxLength(16);
        b.HasIndex(x => new { x.TenantId, x.CreatedAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.TenantId, x.EndpointId, x.CreatedAt });

        b.HasOne<WebhookEndpoint>().WithMany()
            .HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class WebhooksDbContextFactory : IDesignTimeDbContextFactory<WebhooksDbContext>
{
    public WebhooksDbContext CreateDbContext(string[] args)
        => WebhooksDbContext.CreateForMigrations(
            Environment.GetEnvironmentVariable("POYRA_MIGRATIONS_CS")
            ?? "Host=localhost;Port=5442;Database=poyra;Username=poyra;Password=poyra_pw");
}
