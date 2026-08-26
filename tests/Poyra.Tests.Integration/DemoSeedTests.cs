using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Poyra.Api;
using Poyra.Api.Database;
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
