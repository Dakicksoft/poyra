using Npgsql;
using Poyra.Api.Database;
using Poyra.Modules.Connectors;
using Poyra.Modules.Payments;
using Poyra.Modules.Routing;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Tenancy.Domain;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;
using Testcontainers.PostgreSql;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// Gerçek Postgres 18 (Testcontainers). Üretimle aynı rol modeli kurulur:
/// 'poyra' sahip rol migration'ları koşar; testlerin uygulama bağlamları
/// 'poyra_app' (BYPASSRLS'siz) rolüyle bağlanır ki RLS gerçekten sınansın.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithUsername("poyra")
        .WithPassword("poyra_pw")
        .WithDatabase("poyra")
        .Build();

    private readonly IClock _clock = new SystemClock();

    public string OwnerCs { get; private set; } = null!;
    public string AppCs { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        OwnerCs = _container.GetConnectionString();
        AppCs = new NpgsqlConnectionStringBuilder(OwnerCs)
        {
            Username = "poyra_app",
            Password = "poyra_app_pw",
        }.ConnectionString;

        // docker/initdb/01-app-role.sql ile aynı içerik — migration'lardan ÖNCE koşmalı
        await using (var connection = new NpgsqlConnection(OwnerCs))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(AppRoleSql, connection);
            await command.ExecuteNonQueryAsync();
        }

        await DatabaseMigrator.RunAsync(OwnerCs);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public TenancyDbContext CreateTenancy(TenantContext tenant)
        => new(PoyraDb.BuildOptions<TenancyDbContext>(
            AppCs, TenancyDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public PaymentsDbContext CreatePayments(TenantContext tenant)
        => new(PoyraDb.BuildOptions<PaymentsDbContext>(
            AppCs, PaymentsDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public ConnectorsDbContext CreateConnectors(TenantContext tenant)
        => new(PoyraDb.BuildOptions<ConnectorsDbContext>(
            AppCs, ConnectorsDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public RoutingDbContext CreateRouting(TenantContext tenant)
        => new(PoyraDb.BuildOptions<RoutingDbContext>(
            AppCs, RoutingDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public Poyra.Modules.Vault.VaultDbContext CreateVault(TenantContext tenant)
        => new(PoyraDb.BuildOptions<Poyra.Modules.Vault.VaultDbContext>(
            AppCs, Poyra.Modules.Vault.VaultDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public Poyra.Modules.Customers.CustomersDbContext CreateCustomers(TenantContext tenant)
        => new(PoyraDb.BuildOptions<Poyra.Modules.Customers.CustomersDbContext>(
            AppCs, Poyra.Modules.Customers.CustomersDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public Poyra.Modules.Compliance.ComplianceDbContext CreateCompliance(TenantContext tenant)
        => new(PoyraDb.BuildOptions<Poyra.Modules.Compliance.ComplianceDbContext>(
            AppCs, Poyra.Modules.Compliance.ComplianceDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public Poyra.Modules.Risk.RiskDbContext CreateRisk(TenantContext tenant)
        => new(PoyraDb.BuildOptions<Poyra.Modules.Risk.RiskDbContext>(
            AppCs, Poyra.Modules.Risk.RiskDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public Poyra.Modules.Disputes.DisputesDbContext CreateDisputes(TenantContext tenant)
        => new(PoyraDb.BuildOptions<Poyra.Modules.Disputes.DisputesDbContext>(
            AppCs, Poyra.Modules.Disputes.DisputesDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public Poyra.Modules.Ledger.LedgerDbContext CreateLedger(TenantContext tenant)
        => new(PoyraDb.BuildOptions<Poyra.Modules.Ledger.LedgerDbContext>(
            AppCs, Poyra.Modules.Ledger.LedgerDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public Poyra.Modules.Webhooks.WebhooksDbContext CreateWebhooks(TenantContext tenant)
        => new(PoyraDb.BuildOptions<Poyra.Modules.Webhooks.WebhooksDbContext>(
            AppCs, Poyra.Modules.Webhooks.WebhooksDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public Poyra.Modules.PaymentLinks.PaymentLinksDbContext CreatePaymentLinks(TenantContext tenant)
        => new(PoyraDb.BuildOptions<Poyra.Modules.PaymentLinks.PaymentLinksDbContext>(
            AppCs, Poyra.Modules.PaymentLinks.PaymentLinksDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public static TenantContext TenantCtx(Guid tenantId)
    {
        var context = new TenantContext();
        context.Set(tenantId);
        return context;
    }

    /// <summary>tenants platform tablosudur (RLS'siz) — iki işyerini doğrudan ekler.</summary>
    public async Task<(Guid TenantA, Guid TenantB)> SeedTwoTenantsAsync()
    {
        await using var db = CreateTenancy(TenantContext.Platform);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var organization = new Organization { Name = $"Test Org {suffix}" };
        var tenantA = new Tenant { OrganizationId = organization.Id, Name = "İşyeri A", Slug = $"a-{suffix}" };
        var tenantB = new Tenant { OrganizationId = organization.Id, Name = "İşyeri B", Slug = $"b-{suffix}" };

        db.AddRange(organization, tenantA, tenantB);
        await db.SaveChangesAsync();

        return (tenantA.Id, tenantB.Id);
    }

    private const string AppRoleSql = """
        CREATE ROLE poyra_app LOGIN PASSWORD 'poyra_app_pw'
            NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

        GRANT USAGE ON SCHEMA public TO poyra_app;

        ALTER DEFAULT PRIVILEGES FOR ROLE poyra IN SCHEMA public
            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO poyra_app;

        ALTER DEFAULT PRIVILEGES FOR ROLE poyra IN SCHEMA public
            GRANT USAGE, SELECT ON SEQUENCES TO poyra_app;
        """;
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
