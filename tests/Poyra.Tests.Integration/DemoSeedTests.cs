using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Poyra.Api;
using Poyra.Api.Database;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Routing.Dsl;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Tenancy.Domain;
using Poyra.Persistence;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// Demo tohumlaması gerçek Postgres üzerinde. Karar mantığı birim testinde;
/// burada asıl yazma sınanıyor — işyeri gerçekten kuruluyor mu, giriş parolası
/// çalışıyor mu, var olan bir kuruluma dokunuluyor mu.
/// </summary>
[Collection("postgres")]
public sealed class DemoSeedTests : IDisposable
{
    private const string DemoPassword = "cok-uzun-demo-parolasi";

    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;

    public DemoSeedTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _factory = new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Platform:AdminKey", "test-admin-key");
            builder.UseSetting("Poyra:PublicBaseUrl", "http://localhost");
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Poyra:JwtKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            builder.UseSetting("Poyra:VaultKey", Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
        });
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>Veritabanı testler arasında paylaşıldığı için slug her koşuda benzersiz olmalı.</summary>
    private static DemoSeedOptions Options() => new()
    {
        Enabled = true,
        Email = $"demo-{Guid.CreateVersion7():N}@poyra.test",
        Password = DemoPassword,
        TenantSlug = $"demo-{Guid.CreateVersion7():N}"[..24],
    };

    [Fact]
    public async Task Bos_veritabanina_isyeri_ve_giris_kullanicisi_kurmali()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var options = Options();

        await DemoDataWriter.WriteAsync(
            scope.ServiceProvider, options, NullLogger.Instance, CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Slug == options.TenantSlug);
        tenant.ShouldNotBeNull();
        tenant.Name.ShouldBe(options.TenantName);

        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == options.Email);
        user.ShouldNotBeNull();
        user.PasswordHash.ShouldNotBeNullOrWhiteSpace();
        user.PasswordHash.ShouldNotBe(DemoPassword); // düz metin saklanmamalı

        // Parola gerçekten ÇALIŞMALI: hash doğrulanabiliyor mu?
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        hasher.VerifyHashedPassword(user, user.PasswordHash, DemoPassword)
            .ShouldNotBe(PasswordVerificationResult.Failed);
    }

    [Fact]
    public async Task Musteri_ve_farkli_durumlarda_odeme_yazmali()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var options = Options();

        await DemoDataWriter.WriteAsync(
            scope.ServiceProvider, options, NullLogger.Instance, CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenantId = await db.Tenants
            .Where(t => t.Slug == options.TenantSlug)
            .Select(t => t.Id)
            .SingleAsync();

        var ctx = PostgresFixture.TenantCtx(tenantId);

        await using var customers = _fixture.CreateCustomers(ctx);
        (await customers.Customers.CountAsync()).ShouldBeGreaterThanOrEqualTo(4);

        await using var payments = _fixture.CreatePayments(ctx);
        var all = await payments.PaymentIntents.ToListAsync();

        all.Count.ShouldBeGreaterThanOrEqualTo(20);
        all.ShouldContain(x => x.Status == PaymentStatus.Succeeded);
        all.ShouldContain(x => x.Status == PaymentStatus.Failed);

        // Son 30 güne yayılmış olmalı: pano grafiği tek güne yığılmasın.
        all.Select(x => x.CreatedAt.UtcDateTime.Date).Distinct().Count()
            .ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task Pos_baglantisi_rota_kurali_odeme_linki_ve_webhook_yazmali()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var options = Options();

        await DemoDataWriter.WriteAsync(
            scope.ServiceProvider, options, NullLogger.Instance, CancellationToken.None);

        var tenantId = await scope.ServiceProvider.GetRequiredService<TenancyDbContext>()
            .Tenants.Where(t => t.Slug == options.TenantSlug).Select(t => t.Id).SingleAsync();
        var ctx = PostgresFixture.TenantCtx(tenantId);

        await using var connectors = _fixture.CreateConnectors(ctx);
        (await connectors.ConnectorAccounts.CountAsync()).ShouldBe(1);

        await using var routing = _fixture.CreateRouting(ctx);
        var rule = await routing.RoutingRules.SingleAsync();
        rule.IsActive.ShouldBeTrue();

        // Belge motorun GERÇEK şemasına uymalı — yoksa panelde açılmaz.
        RuleDocument.Parse(rule.Document).Rules.ShouldNotBeEmpty();

        await using var links = _fixture.CreatePaymentLinks(ctx);
        (await links.PaymentLinks.CountAsync()).ShouldBe(1);

        await using var webhooks = _fixture.CreateWebhooks(ctx);
        var endpoint = await webhooks.WebhookEndpoints.SingleAsync();
        endpoint.Active.ShouldBeTrue();
        endpoint.EventTypes.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Isyeri_varken_tohumlayici_hicbir_sey_yazmamali()
    {
        // Önce bir işyeri kur ki "gerçek kurulum" durumu deterministik olsun.
        await _fixture.SeedTwoTenantsAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var options = Options();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var beforeCount = await db.Tenants.CountAsync();

        var result = await DemoDataSeeder.SeedAsync(
            options,
            ct => DemoDataWriter.TenantExistsAsync(scope.ServiceProvider, ct),
            ct => DemoDataWriter.WriteAsync(scope.ServiceProvider, options, NullLogger.Instance, ct),
            NullLogger.Instance);

        result.ShouldBe(DemoSeedOutcome.TenantExists);

        var afterCount = await db.Tenants.CountAsync();
        afterCount.ShouldBe(beforeCount);
    }
}
