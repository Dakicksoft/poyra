using Microsoft.Extensions.Logging.Abstractions;
using Poyra.Persistence;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Demo tohumlayıcısının KARAR mantığı. Veri yazma tarafı DemoDataWriter'da ve
/// entegrasyon testinde sınanır; burada yalnız "kurulsun mu?" sorusu ele alınır.
/// </summary>
public sealed class DemoDataSeederTests
{
    private static DemoSeedOptions Valid() => new()
    {
        Enabled = true,
        Email = "demo@poyra.test",
        Password = "cok-uzun-demo-parolasi",
    };

    private static Task<DemoSeedOutcome> Run(
        DemoSeedOptions options, bool tenantExists, Action? onSeed = null)
        => DemoDataSeeder.SeedAsync(
            options,
            _ => Task.FromResult(tenantExists),
            _ => { onSeed?.Invoke(); return Task.CompletedTask; },
            NullLogger.Instance);

    [Fact]
    public async Task Bayrak_kapaliyken_hic_dokunmamali()
    {
        var touched = false;
        var result = await Run(Valid() with { Enabled = false }, tenantExists: false,
            onSeed: () => touched = true);

        result.ShouldBe(DemoSeedOutcome.Disabled);
        touched.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, "parola")]
    [InlineData("demo@poyra.test", null)]
    [InlineData("", "parola")]
    [InlineData("demo@poyra.test", "  ")]
    public async Task Eksik_giris_bilgisi_varsa_atlamali(string? email, string? password)
    {
        var touched = false;
        var result = await Run(
            Valid() with { Email = email, Password = password }, tenantExists: false,
            onSeed: () => touched = true);

        result.ShouldBe(DemoSeedOutcome.MissingSettings);
        touched.ShouldBeFalse();
    }

    [Fact]
    public async Task Tek_bir_isyeri_bile_varsa_hicbir_sey_yazmamali()
    {
        var touched = false;
        var result = await Run(Valid(), tenantExists: true, onSeed: () => touched = true);

        result.ShouldBe(DemoSeedOutcome.TenantExists);
        touched.ShouldBeFalse();
    }

    [Fact]
    public async Task Bos_veritabaninda_tohumlamali()
    {
        var touched = false;
        var result = await Run(Valid(), tenantExists: false, onSeed: () => touched = true);

        result.ShouldBe(DemoSeedOutcome.Seeded);
        touched.ShouldBeTrue();
    }

    [Fact]
    public async Task Tohumlama_patlarsa_acilisi_dusurmemeli()
    {
        var result = await DemoDataSeeder.SeedAsync(
            Valid(),
            _ => Task.FromResult(false),
            _ => throw new InvalidOperationException("tohumlama patladı"),
            NullLogger.Instance);

        result.ShouldBe(DemoSeedOutcome.Failed);
    }
}
