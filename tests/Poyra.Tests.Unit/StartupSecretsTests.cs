using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Poyra.SharedKernel.Security;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Açılış doğrulaması. Eksik/zayıf anahtarla AÇILMAK, hatayı ilk gerçek ödemeye
/// ertelemektir: kimlik şifrelenemez, belirteç doğrulanamaz, kart zarfı açılamaz —
/// hepsi müşteri kasadayken patlar. Üretimde uygulama hiç açılmamalı.
/// </summary>
public sealed class StartupSecretsTests
{
    private static readonly string Good = StartupSecrets.GenerateKey();

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Poyra";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    [Fact]
    public void Eksik_anahtar_sorun_olarak_bildirilmeli()
    {
        var problems = StartupSecrets.Validate(
            Config(("Poyra:CredentialKey", null)), new Env("Production"), "CredentialKey");

        problems.ShouldContain(p => p.Contains("Poyra:CredentialKey") && p.Contains("tanımlı değil"));
    }

    [Fact]
    public void Base64_olmayan_ve_yanlis_uzunluktaki_anahtar_reddedilmeli()
    {
        StartupSecrets.Validate(Config(("Poyra:VaultKey", "bu base64 değil!")),
                new Env("Production"), "VaultKey")
            .ShouldContain(p => p.Contains("base64"));

        StartupSecrets.Validate(Config(("Poyra:VaultKey", Convert.ToBase64String(new byte[16]))),
                new Env("Production"), "VaultKey")
            .ShouldContain(p => p.Contains("32 bayt"));
    }

    [Fact]
    public void Bilinen_ornek_anahtarlar_yakalanmali()
    {
        // Testlerde ve appsettings.Development.json'da kullanılan anahtarlar —
        // üretime kopyalanması en olası hata budur
        var weak = new[]
        {
            Convert.ToBase64String(new byte[32]),
            Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()),
            "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=",
        };

        foreach (var key in weak)
        {
            StartupSecrets.Validate(Config(("Poyra:CredentialKey", key)),
                    new Env("Production"), "CredentialKey")
                .ShouldContain(p => p.Contains("ÖRNEK anahtar"), $"'{key}' zayıf sayılmalı");
        }
    }

    [Fact]
    public void Sahip_rolle_baglanti_yakalanmali()
    {
        // Sahip rol RLS'i ATLAR: işyeri yalıtımının ikinci katmanı sessizce kaybolur
        var problems = StartupSecrets.Validate(
            Config(
                ("Poyra:CredentialKey", Good),
                ("ConnectionStrings:Poyra", "Host=db;Database=poyra;Username=poyra;Password=x")),
            new Env("Production"), "CredentialKey");

        problems.ShouldContain(p => p.Contains("poyra_app") && p.Contains("RLS"));
    }

    /// <summary>
    /// Bu test bir GÖZDEN GEÇİRME BULGUSUYLA doğdu: denetim yalnız 'poyra' sahip rolünü
    /// arayan bir KARA LİSTEydi ve geliştirme ayarındaki <c>postgres</c> (süper kullanıcı,
    /// sahip rolden bile yetkili) süzgeçten geçti — RLS her geliştiricinin makinesinde
    /// kapalıydı ve kimse fark etmedi. Yasaklı adları saymak, bir sonraki adı kaçırmaktır.
    /// </summary>
    [Theory]
    [InlineData("postgres", "süper kullanıcı — RLS'i tamamen atlar")]
    [InlineData("poyra", "sahip rol — kendi tablolarında RLS'e tabi değil")]
    [InlineData("admin", "adı bilinmeyen ama poyra_app olmayan her rol")]
    public void Poyra_app_disindaki_her_rol_yakalanmali(string user, string _)
    {
        var problems = StartupSecrets.Validate(
            Config(
                ("Poyra:CredentialKey", Good),
                ("ConnectionStrings:Poyra", $"Host=db;Database=poyra;Username={user};Password=x")),
            new Env("Production"), "CredentialKey");

        problems.ShouldContain(p => p.Contains("poyra_app") && p.Contains(user));
    }

    [Fact]
    public void User_ID_yazimi_da_taninmali()
    {
        // Npgsql "User ID=" yazımını da kabul eder; denetim ondan kaçmamalı
        var problems = StartupSecrets.Validate(
            Config(
                ("Poyra:CredentialKey", Good),
                ("ConnectionStrings:Poyra", "Host=db;Database=poyra;User ID=postgres;Password=x")),
            new Env("Production"), "CredentialKey");

        problems.ShouldContain(p => p.Contains("poyra_app"));
    }

    [Fact]
    public void Uygulama_rolu_ile_baglanti_sorun_cikarmamali()
    {
        var problems = StartupSecrets.Validate(
            Config(
                ("Poyra:CredentialKey", Good),
                ("Platform:AdminKey", "uzun-rastgele-anahtar"),
                ("ConnectionStrings:Poyra", "Host=db;Database=poyra;Username=poyra_app;Password=x")),
            new Env("Production"), "CredentialKey");

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Uretimde_platform_anahtari_zorunlu_gelistirmede_degil()
    {
        StartupSecrets.Validate(Config(("Poyra:CredentialKey", Good)),
                new Env("Production"), "CredentialKey")
            .ShouldContain(p => p.Contains("Platform:AdminKey"));

        StartupSecrets.Validate(Config(("Poyra:CredentialKey", Good)),
                new Env("Development"), "CredentialKey")
            .ShouldBeEmpty();
    }

    [Fact]
    public void Uretimde_sorun_varsa_uygulama_acilmamali()
    {
        var warnings = new List<string>();

        var ex = Should.Throw<InvalidOperationException>(() => StartupSecrets.EnsureOrThrow(
            Config(("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]))),
            new Env("Production"), warnings.Add, "CredentialKey"));

        ex.Message.ShouldContain("ÖRNEK anahtar");
        ex.Message.ShouldContain("ilk gerçek ödemeye ertelemektir");
        warnings.ShouldBeEmpty("üretimde uyarıp devam edilmez, açılış durdurulur");
    }

    [Fact]
    public void Gelistirmede_uyarilir_ama_acilis_engellenmez()
    {
        var warnings = new List<string>();

        // Kurulum akışını kilitlemek yanlıştır: geliştirici anahtarları sonra doldurur
        Should.NotThrow(() => StartupSecrets.EnsureOrThrow(
            Config(("Poyra:CredentialKey", null)),
            new Env("Development"), warnings.Add, "CredentialKey"));

        warnings.ShouldHaveSingleItem().ShouldContain("Üretimde bu açılışı engellerdi");
    }

    [Fact]
    public void Uretilen_anahtar_gecerli_olmali()
    {
        var key = StartupSecrets.GenerateKey();

        Convert.FromBase64String(key).Length.ShouldBe(32);
        StartupSecrets.GenerateKey().ShouldNotBe(key); // her çağrı yeni

        StartupSecrets.Validate(
                Config(("Poyra:CredentialKey", key), ("Platform:AdminKey", "x")),
                new Env("Production"), "CredentialKey")
            .ShouldBeEmpty();
    }
}
