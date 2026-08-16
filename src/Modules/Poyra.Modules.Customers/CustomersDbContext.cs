using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Customers.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Customers;

public sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_customers";

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Mandate> Mandates => Set<Mandate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CustomersDbContext).Assembly);
    }

    public static CustomersDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<CustomersDbContext>(connectionString, MigrationsHistoryTable),
            TenantContext.Platform);
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Ref).HasMaxLength(100);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(2000);

        // Referans işyeri içinde tekildir — aynı müşteri iki kez kaydedilirse
        // ödeme geçmişi ikiye bölünür ve hiçbiri tam görünmez
        b.HasIndex(x => new { x.TenantId, x.Ref }).IsUnique();
    }
}

internal sealed class MandateConfiguration : IEntityTypeConfiguration<Mandate>
{
    public void Configure(EntityTypeBuilder<Mandate> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PublicId).HasComputedColumnSql("'mnd_' || replace(id::text, '-', '')", stored: true);
        b.HasIndex(x => x.PublicId).IsUnique();
        b.Property(x => x.CustomerRef).HasMaxLength(100);
        b.Property(x => x.CardToken).HasMaxLength(64);
        b.Property(x => x.TextVersion).HasMaxLength(40);
        b.Property(x => x.AcceptedIp).HasMaxLength(45);
        b.Property(x => x.RevokedReason).HasMaxLength(500);
        b.Property(x => x.Type)
            .HasConversion(t => MandateTypeMap.ToDb[t], t => MandateTypeMap.FromDb[t])
            .HasMaxLength(20);

        b.HasIndex(x => new { x.TenantId, x.CustomerRef });

        // Aynı kart için AKTİF tek talimat: iki aktif onay, hangisinin dayanak
        // olduğunu belirsiz bırakır ve itiraz savunmasını zayıflatır
        b.HasIndex(x => new { x.TenantId, x.CustomerRef, x.CardToken })
            .IsUnique()
            .HasFilter("revoked_at IS NULL");
    }
}
