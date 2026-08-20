using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Poyra.Modules.Installments.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Installments;

public sealed class InstallmentsDbContext(DbContextOptions<InstallmentsDbContext> options, TenantContext tenant)
    : ModuleDbContext(options, tenant)
{
    public const string MigrationsHistoryTable = "__ef_migrations_installments";

    public DbSet<CardBin> CardBins => Set<CardBin>();
    public DbSet<InstallmentScheme> InstallmentSchemes => Set<InstallmentScheme>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InstallmentsDbContext).Assembly);
    }

    public static InstallmentsDbContext CreateForMigrations(string connectionString)
        => new(PoyraDb.BuildOptions<InstallmentsDbContext>(connectionString, MigrationsHistoryTable), TenantContext.Platform);
}

internal sealed class CardBinConfiguration : IEntityTypeConfiguration<CardBin>
{
    public void Configure(EntityTypeBuilder<CardBin> b)
    {
        b.HasKey(x => x.Bin);
        b.Property(x => x.Bin).HasMaxLength(8).ValueGeneratedNever();
        b.Property(x => x.BankCode).HasMaxLength(8);
        b.Property(x => x.BankName).HasMaxLength(100);
        b.Property(x => x.Program).HasMaxLength(20);
        b.Property(x => x.Brand).HasMaxLength(20);
        b.Property(x => x.CardType).HasMaxLength(10);

        // Varsayılan veritabanı düzeyinde de durur: alan eklenmeden önceki BIN'ler ve
        // ham SQL ile eklenen kayıtlar TR olur — yurt dışı kart ancak AÇIKÇA işaretlenir.
        b.Property(x => x.Country).HasMaxLength(2).IsFixedLength().HasDefaultValue("TR");
        b.HasIndex(x => x.Program);
    }
}

internal sealed class InstallmentSchemeConfiguration : IEntityTypeConfiguration<InstallmentScheme>
{
    public void Configure(EntityTypeBuilder<InstallmentScheme> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Program).HasMaxLength(20);
        b.HasIndex(x => new { x.TenantId, x.ConnectorAccountId, x.Program, x.InstallmentCount }).IsUnique();
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_installment_schemes_count", "installment_count BETWEEN 2 AND 12");
            t.HasCheckConstraint("ck_installment_schemes_rate", "customer_rate_bps BETWEEN 0 AND 10000");
        });
    }
}

public sealed class InstallmentsDbContextFactory : IDesignTimeDbContextFactory<InstallmentsDbContext>
{
    public InstallmentsDbContext CreateDbContext(string[] args)
        => InstallmentsDbContext.CreateForMigrations(
            Environment.GetEnvironmentVariable("POYRA_MIGRATIONS_CS")
            ?? "Host=localhost;Port=5442;Database=poyra;Username=poyra;Password=poyra_pw");
}
