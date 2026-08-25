# Demo Veri Tohumlama — Uygulama Planı

> **Ajan çalışanlar için:** GEREKLİ ALT BECERİ: Bu planı görev görev uygulamak için
> `superpowers:subagent-driven-development` (önerilen) ya da `superpowers:executing-plans`
> kullanın. Adımlar takip için onay kutusu (`- [ ]`) biçimindedir.

**Hedef:** `.env`'deki bir bayrak açıkken, BOŞ bir veritabanına panel tanıtımı için
yeterli demo verisi kuran; giriş bilgilerini yine `.env`'den alan bir tohumlayıcı eklemek.

**Mimari:** Tohumlayıcı `Poyra.Api` açılışında, `DatabaseRoleGuard`'dan sonra koşar.
Tüm tohumlama boyunca açık tutulan ayrı bir Npgsql bağlantısında **oturum düzeyi
advisory lock** alınır; böylece birden çok API kopyası aynı anda kalksa bile yalnız biri
tohumlar. Kilit altında "hiç işyeri var mı?" kontrolü yapılır — bir tane bile varsa
hiçbir şey yazılmadan çıkılır. Silme kodu hiç yazılmaz.

**Teknoloji:** .NET 10 · EF Core (Npgsql) · FastEndpoints · xunit + Shouldly ·
Testcontainers (entegrasyon)

## Global Kısıtlar

- Yorumlar, günlük mesajları ve test adları **Türkçe** — deponun mevcut düzeni.
- Test adları alt çizgili Türkçe cümle: `Bayrak_kapaliyken_hic_dokunmamali`.
- Birim testleri **Docker istemez**; gerçek Postgres gerektiren her şey entegrasyon projesine gider.
- Commit mesajları Conventional Commits + Türkçe: `feat(api): …`, `test(api): …`.
- **Co-Authored-By satırı EKLENMEZ.**
- Doğrudan `develop` dalına commit edilir. Push kullanıcı onayı ister — plan hiçbir adımda push etmez.
- Hiçbir görev veri SİLMEZ. `DELETE`, `ExecuteDelete`, `RemoveRange` bu planda yasaktır.
- Tohumlayıcı asla açılışı düşürmez: her hata yakalanır, uyarı olarak günlüğe yazılır.
- Uygulama veritabanına `poyra_app` rolüyle bağlanır (RLS'e tabi). Tohumlama da bu rolle
  yazar — ayrıcalıklı bir yola sapılmaz.

## Dosya Yapısı

| Dosya | Sorumluluk |
|---|---|
| `src/Poyra.Api/Database/DemoSeedOptions.cs` (yeni) | Ayar kaydı + sonuç enum'u. Bağımlılığı yok. |
| `src/Poyra.Api/Database/DemoDataSeeder.cs` (yeni) | Karar mantığı + kilit + sıralama. Veri yazmaz. |
| `src/Poyra.Api/Database/DemoDataWriter.cs` (yeni) | Asıl satırları yazar; modül modül bölünmüş. |
| `src/Poyra.Api/Program.cs` (değişir) | Açılış kancası. |
| `.env.example`, `scripts/anahtar-uret.sh` (değişir) | Ayar yüzeyi. |
| `docker-compose.dokploy.yml`, `docker-compose.prod.yml` (değişir) | Değişkenleri yalnız `api` servisine geçir. |
| `tests/Poyra.Tests.Unit/DemoDataSeederTests.cs` (yeni) | Karar mantığı — Docker'sız. |
| `tests/Poyra.Tests.Integration/DemoSeedTests.cs` (yeni) | Gerçek Postgres'te uçtan uca. |

`DemoDataSeeder` **karar verir**, `DemoDataWriter` **yazar**. Bu ayrım sayesinde kararlar
veritabanı olmadan birim testiyle sınanır; yazma tarafı entegrasyon testine kalır.

---

### Görev 1: Ayar yüzeyi ve karar mantığı

**Dosyalar:**
- Oluştur: `src/Poyra.Api/Database/DemoSeedOptions.cs`
- Oluştur: `src/Poyra.Api/Database/DemoDataSeeder.cs`
- Test: `tests/Poyra.Tests.Unit/DemoDataSeederTests.cs`
- Değiştir: `.env.example`
- Değiştir: `scripts/anahtar-uret.sh`

**Arayüzler:**
- Üretir: `DemoSeedOptions` (kayıt), `DemoSeedOutcome` (enum),
  `DemoDataSeeder.SeedAsync(DemoSeedOptions, Func<CancellationToken,Task<bool>>, Func<CancellationToken,Task>, ILogger, CancellationToken) → Task<DemoSeedOutcome>`.
  Görev 2 ve 3 bu imzayı kullanır.

- [ ] **Adım 1: Başarısız testi yaz**

`tests/Poyra.Tests.Unit/DemoDataSeederTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Poyra.Api.Database;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class DemoDataSeederTests
{
    private static DemoSeedOptions Gecerli() => new()
    {
        Enabled = true,
        Email = "demo@poyra.test",
        Password = "cok-uzun-demo-parolasi",
    };

    private static Task<DemoSeedOutcome> Kos(
        DemoSeedOptions ayarlar, bool isyeriVar, Action? tohumlandi = null)
        => DemoDataSeeder.SeedAsync(
            ayarlar,
            _ => Task.FromResult(isyeriVar),
            _ => { tohumlandi?.Invoke(); return Task.CompletedTask; },
            NullLogger.Instance);

    [Fact]
    public async Task Bayrak_kapaliyken_hic_dokunmamali()
    {
        var dokunuldu = false;
        var sonuc = await Kos(Gecerli() with { Enabled = false }, isyeriVar: false,
            tohumlandi: () => dokunuldu = true);

        sonuc.ShouldBe(DemoSeedOutcome.Kapali);
        dokunuldu.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, "parola")]
    [InlineData("demo@poyra.test", null)]
    [InlineData("", "parola")]
    [InlineData("demo@poyra.test", "  ")]
    public async Task Eksik_giris_bilgisi_varsa_atlamali(string? eposta, string? parola)
    {
        var dokunuldu = false;
        var sonuc = await Kos(
            Gecerli() with { Email = eposta, Password = parola }, isyeriVar: false,
            tohumlandi: () => dokunuldu = true);

        sonuc.ShouldBe(DemoSeedOutcome.EksikAyar);
        dokunuldu.ShouldBeFalse();
    }

    [Fact]
    public async Task Tek_bir_isyeri_bile_varsa_hicbir_sey_yazmamali()
    {
        var dokunuldu = false;
        var sonuc = await Kos(Gecerli(), isyeriVar: true, tohumlandi: () => dokunuldu = true);

        sonuc.ShouldBe(DemoSeedOutcome.IsyeriVar);
        dokunuldu.ShouldBeFalse();
    }

    [Fact]
    public async Task Bos_veritabaninda_tohumlamali()
    {
        var dokunuldu = false;
        var sonuc = await Kos(Gecerli(), isyeriVar: false, tohumlandi: () => dokunuldu = true);

        sonuc.ShouldBe(DemoSeedOutcome.Tohumlandi);
        dokunuldu.ShouldBeTrue();
    }

    [Fact]
    public async Task Tohumlama_patlarsa_acilisi_dusurmemeli()
    {
        var sonuc = await DemoDataSeeder.SeedAsync(
            Gecerli(),
            _ => Task.FromResult(false),
            _ => throw new InvalidOperationException("tohumlama patladı"),
            NullLogger.Instance);

        sonuc.ShouldBe(DemoSeedOutcome.Basarisiz);
    }
}
```

- [ ] **Adım 2: Testi koş, başarısız olduğunu doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Unit --filter "FullyQualifiedName~DemoDataSeederTests"`

Beklenen: `error CS0246: 'DemoSeedOptions' türü bulunamadı` (tür henüz yok).

- [ ] **Adım 3: Ayar kaydını yaz**

`src/Poyra.Api/Database/DemoSeedOptions.cs`:

```csharp
namespace Poyra.Api.Database;

/// <summary>Demo tohumlamasının sonucu — günlüğe ve testlere aynı kelimeyle döner.</summary>
public enum DemoSeedOutcome
{
    /// <summary>Bayrak kapalı; hiç bakılmadı.</summary>
    Kapali,

    /// <summary>Bayrak açık ama e-posta ya da parola verilmemiş.</summary>
    EksikAyar,

    /// <summary>Veritabanında en az bir işyeri var; hiçbir şey yazılmadı.</summary>
    IsyeriVar,

    /// <summary>Demo verisi kuruldu.</summary>
    Tohumlandi,

    /// <summary>Tohumlama sırasında hata çıktı; açılış sürdürüldü.</summary>
    Basarisiz,
}

/// <summary>
/// Poyra:Demo bölümünden okunur. Bayrak yalnız TANITIM kurulumlarında açılır:
/// veritabanı boş değilse tohumlayıcı zaten hiçbir şey yapmaz, ama bayrağı üretimde
/// açık unutmak yine de istenmez.
/// </summary>
public sealed record DemoSeedOptions
{
    public const string Section = "Poyra:Demo";

    public bool Enabled { get; init; }
    public string? Email { get; init; }
    public string? Password { get; init; }
    public string TenantName { get; init; } = "Poyra Demo";
    public string TenantSlug { get; init; } = "demo";
    public string OwnerName { get; init; } = "Demo Kullanıcı";
}
```

- [ ] **Adım 4: Karar mantığını yaz**

`src/Poyra.Api/Database/DemoDataSeeder.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Poyra.Api.Database;

/// <summary>
/// Demo verisinin KURULUP kurulmayacağına karar verir; satırları kendisi yazmaz
/// (onu DemoDataWriter yapar). Bu ayrım sayesinde kararlar veritabanı olmadan sınanır.
/// </summary>
public static class DemoDataSeeder
{
    public static async Task<DemoSeedOutcome> SeedAsync(
        DemoSeedOptions options,
        Func<CancellationToken, Task<bool>> isyeriVarMi,
        Func<CancellationToken, Task> tohumla,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
            return DemoSeedOutcome.Kapali;

        if (string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrWhiteSpace(options.Password))
        {
            logger.LogWarning(
                "Demo tohumlaması açık ama {Section}:Email / :Password verilmemiş — atlanıyor.",
                DemoSeedOptions.Section);
            return DemoSeedOutcome.EksikAyar;
        }

        try
        {
            // Bir tane bile işyeri varsa burası gerçek bir kurulumdur: dokunulmaz.
            if (await isyeriVarMi(cancellationToken))
            {
                logger.LogInformation(
                    "Demo tohumlaması atlandı: veritabanında zaten işyeri var.");
                return DemoSeedOutcome.IsyeriVar;
            }

            await tohumla(cancellationToken);
            logger.LogInformation("Demo verisi kuruldu (giriş: {Email}).", options.Email);
            return DemoSeedOutcome.Tohumlandi;
        }
        catch (Exception exception)
        {
            // Demo verisi açılışı düşürmeye değmez: uygulama demo verisi olmadan da çalışır.
            logger.LogWarning(exception, "Demo tohumlaması başarısız — açılış sürdürülüyor.");
            return DemoSeedOutcome.Basarisiz;
        }
    }
}
```

- [ ] **Adım 5: Testleri koş, geçtiğini doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Unit --filter "FullyQualifiedName~DemoDataSeederTests"`

Beklenen: `Başarılı! - Başarısız: 0, Başarılı: 8`

- [ ] **Adım 6: `.env.example`'a ayarları ekle**

`.env.example` içinde `# --- E-Posta` satırının ÜSTÜNE ekle:

```
# --- Demo verisi (yalnız TANITIM kurulumları) --------------------------------
# true yapılırsa ve veritabanı BOŞSA panel tanıtımı için örnek veri kurulur.
# Bir tane bile işyeri varsa hiçbir şey yazılmaz — bayrak açık unutulsa da
# gerçek bir kurulum kirlenmez. Demoyu tazelemek = birimi silip yeniden dağıtmak.
POYRA_DEMO=false
POYRA_DEMO_EMAIL=demo@poyra.test
# Parolayı anahtar-uret.sh üretir. Ekranda paylaşacaksanız elle akılda kalır bir
# değerle değiştirin — doğrulayıcı en az 10 karakter istiyor.
POYRA_DEMO_PASSWORD=

```

- [ ] **Adım 7: `scripts/anahtar-uret.sh`'e demo parolasını ekle**

Dosyanın SONUNA ekle:

```sh
# Demo giriş parolası — yalnız POYRA_DEMO=true iken kullanılır.
echo "POYRA_DEMO_PASSWORD=$(openssl rand -base64 18 | tr -d '/+=')"
```

- [ ] **Adım 8: Betiğin çalıştığını doğrula**

Çalıştır: `./scripts/anahtar-uret.sh | grep POYRA_DEMO_PASSWORD`

Beklenen: `POYRA_DEMO_PASSWORD=` sonrası ~24 karakterlik alfanümerik dizi.

- [ ] **Adım 9: Commit**

```bash
git add src/Poyra.Api/Database/DemoSeedOptions.cs src/Poyra.Api/Database/DemoDataSeeder.cs tests/Poyra.Tests.Unit/DemoDataSeederTests.cs .env.example scripts/anahtar-uret.sh
git commit -m "feat(api): demo tohumlama ayarları ve karar mantığı"
```

---

### Görev 2: İşyeri ve giriş kullanıcısı

**Dosyalar:**
- Oluştur: `src/Poyra.Api/Database/DemoDataWriter.cs`
- Test: `tests/Poyra.Tests.Integration/DemoSeedTests.cs`

**Arayüzler:**
- Tüketir: Görev 1'den `DemoSeedOptions`, `DemoSeedOutcome`, `DemoDataSeeder.SeedAsync`.
- Üretir: `DemoDataWriter.IsyeriVarMiAsync(IServiceProvider, CancellationToken) → Task<bool>`
  ve `DemoDataWriter.YazAsync(IServiceProvider, DemoSeedOptions, ILogger, CancellationToken) → Task`.
  Görev 3 (`Program.cs` kancası) bu iki imzayı çağırır; Görev 4 ve 5 `YazAsync` gövdesini büyütür.

Mevcut `CreateTenantCommand` kullanılır — organizasyon, işyeri, varsayılan iş profili,
API anahtarı ve parolası hash'lenmiş sahip kullanıcıyı birlikte kurar. Elle `User` üretip
parola hash'lemek bu doğrulanmış yolu atlamak olurdu.

- [ ] **Adım 1: Başarısız entegrasyon testini yaz**

`tests/Poyra.Tests.Integration/DemoSeedTests.cs`:

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Poyra.Api.Database;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Tenancy.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

[Collection(nameof(PostgresFixture))]
public sealed class DemoSeedTests(PostgresFixture fixture)
{
    private static DemoSeedOptions Ayarlar() => new()
    {
        Enabled = true,
        Email = "demo@poyra.test",
        Password = "cok-uzun-demo-parolasi",
        TenantSlug = $"demo-{Guid.CreateVersion7():N}"[..20],
    };

    [Fact]
    public async Task Bos_veritabanina_isyeri_ve_giris_kullanicisi_kurmali()
    {
        await using var kapsam = fixture.CreateApiScope();
        var ayarlar = Ayarlar();

        await DemoDataWriter.YazAsync(
            kapsam.ServiceProvider, ayarlar, NullLogger.Instance, TestContext.Current.CancellationToken);

        var db = kapsam.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var isyeri = await db.Tenants.SingleOrDefaultAsync(
            t => t.Slug == ayarlar.TenantSlug, TestContext.Current.CancellationToken);
        isyeri.ShouldNotBeNull();

        var kullanici = await db.Users.SingleOrDefaultAsync(
            u => u.Email == ayarlar.Email, TestContext.Current.CancellationToken);
        kullanici.ShouldNotBeNull();
        kullanici.PasswordHash.ShouldNotBeNullOrWhiteSpace();
        kullanici.PasswordHash.ShouldNotBe(ayarlar.Password); // düz metin saklanmamalı

        // Parola gerçekten ÇALIŞMALI: hash doğrulanabiliyor mu?
        var hasher = kapsam.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        hasher.VerifyHashedPassword(kullanici, kullanici.PasswordHash, ayarlar.Password!)
            .ShouldNotBe(PasswordVerificationResult.Failed);
    }

    [Fact]
    public async Task Isyeri_varken_tohumlayici_hicbir_sey_yazmamali()
    {
        await using var kapsam = fixture.CreateApiScope();
        var ayarlar = Ayarlar();

        var oncekiSayi = await kapsam.ServiceProvider
            .GetRequiredService<TenancyDbContext>().Tenants
            .CountAsync(TestContext.Current.CancellationToken);

        var sonuc = await DemoDataSeeder.SeedAsync(
            ayarlar,
            ct => DemoDataWriter.IsyeriVarMiAsync(kapsam.ServiceProvider, ct),
            ct => DemoDataWriter.YazAsync(kapsam.ServiceProvider, ayarlar, NullLogger.Instance, ct),
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        sonuc.ShouldBe(DemoSeedOutcome.IsyeriVar);

        var sonrakiSayi = await kapsam.ServiceProvider
            .GetRequiredService<TenancyDbContext>().Tenants
            .CountAsync(TestContext.Current.CancellationToken);
        sonrakiSayi.ShouldBe(oncekiSayi);
    }
}
```

**Not:** `PostgresFixture` şu an bir `CreateApiScope()` üyesi sunmuyor. Adım 3'te eklenecek.

- [ ] **Adım 2: Testi koş, başarısız olduğunu doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~DemoSeedTests"`

Beklenen: `error CS1061: 'PostgresFixture' 'CreateApiScope' tanımı içermiyor`.

- [ ] **Adım 3: Fikstüre API kapsamı ekle**

`tests/Poyra.Tests.Integration/PostgresFixture.cs` içine, sınıfın sonuna ekle:

```csharp
    /// <summary>
    /// Demo tohumlayıcısı IDispatcher üzerinden CreateTenantCommand gönderir; bu yüzden
    /// yalnız DbContext değil, CQRS kayıtları da olan bir kapsam gerekir.
    /// </summary>
    public AsyncServiceScope CreateApiScope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Poyra:CredentialKey"] = Convert.ToBase64String(new byte[32]),
                ["Poyra:JwtKey"] = Convert.ToBase64String(new byte[32]),
                ["Poyra:VaultKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build());
        services.AddScoped<TenantContext>();
        services.AddScoped(_ => CreateTenancy(new TenantContext()));
        services.AddTenancyModule();
        services.AddPoyraCqrs(TenancyModule.Assembly);

        return new AsyncServiceScope(services.BuildServiceProvider().CreateScope());
    }
```

Dosyanın başına gereken `using`'leri ekle:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Poyra.SharedKernel.Cqrs;
```

- [ ] **Adım 4: Testi koş — bu sefer yazıcı eksik olmalı**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~DemoSeedTests"`

Beklenen: `error CS0103: 'DemoDataWriter' adı geçerli değil`.

- [ ] **Adım 5: Yazıcıyı yaz**

`src/Poyra.Api/Database/DemoDataWriter.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Tenancy.Features.CreateTenant;
using Poyra.SharedKernel.Cqrs;

namespace Poyra.Api.Database;

/// <summary>
/// Demo satırlarını yazar. Hiçbir metodu veri SİLMEZ; tohumlayıcı zaten yalnız boş
/// veritabanında çağırır.
/// </summary>
public static class DemoDataWriter
{
    public static async Task<bool> IsyeriVarMiAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TenancyDbContext>();
        return await db.Tenants.AnyAsync(cancellationToken);
    }

    public static async Task YazAsync(
        IServiceProvider services,
        DemoSeedOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var dispatcher = services.GetRequiredService<IDispatcher>();

        // Mevcut ve doğrulanmış yol: organizasyon + işyeri + varsayılan profil +
        // API anahtarı + parolası hash'lenmiş sahip kullanıcı birlikte kurulur.
        var isyeri = await dispatcher.Send(
            new CreateTenantCommand(
                options.TenantName,
                options.TenantSlug,
                options.Email,
                options.Password,
                options.OwnerName),
            cancellationToken);

        logger.LogInformation(
            "Demo işyeri kuruldu: {Slug} ({TenantId}).", isyeri.Slug, isyeri.TenantId);
    }
}
```

- [ ] **Adım 6: Testleri koş, geçtiğini doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~DemoSeedTests"`

Beklenen: `Başarılı! - Başarısız: 0, Başarılı: 2`

- [ ] **Adım 7: Commit**

```bash
git add src/Poyra.Api/Database/DemoDataWriter.cs tests/Poyra.Tests.Integration/DemoSeedTests.cs tests/Poyra.Tests.Integration/PostgresFixture.cs
git commit -m "feat(api): demo işyeri ve giriş kullanıcısı tohumlaması"
```

---

### Görev 3: Açılış kancası, advisory lock ve compose bağlantısı

Bu görev bittiğinde özellik uçtan uca çalışır: `.env`'de bayrak açılır, dağıtılır, panele
girilir. Görev 4 ve 5 yalnız veri zenginleştirir.

**Dosyalar:**
- Değiştir: `src/Poyra.Api/Program.cs:210` (`DatabaseRoleGuard` çağrısından hemen sonra)
- Değiştir: `src/Poyra.Api/Database/DemoDataWriter.cs` (kilit yardımcısı eklenir)
- Değiştir: `docker-compose.dokploy.yml`, `docker-compose.prod.yml`

**Arayüzler:**
- Tüketir: Görev 1'den `DemoSeedOptions`/`DemoDataSeeder`, Görev 2'den `DemoDataWriter`.
- Üretir: `DemoDataWriter.KilitliKosAsync(string connectionString, Func<Task> is, CancellationToken) → Task`.

- [ ] **Adım 1: Kilit yardımcısını yaz**

`src/Poyra.Api/Database/DemoDataWriter.cs` içine, sınıfın sonuna ekle:

```csharp
    /// <summary>
    /// Verilen işi oturum düzeyi advisory lock altında koşturur.
    ///
    /// Her modülün kendi DbContext'i (dolayısıyla kendi bağlantısı) var; tek transaction
    /// hepsini kapsayamaz. Bu yüzden kilit, tohumlama boyunca AÇIK TUTULAN ayrı bir
    /// bağlantıda alınır. Böylece iki API kopyası aynı anda kalksa bile "işyeri var mı?"
    /// kontrolü ile yazma arasına başka kimse giremez.
    /// </summary>
    public static async Task KilitliKosAsync(
        string connectionString, Func<Task> islem, CancellationToken cancellationToken)
    {
        await using var kilitBaglantisi = new NpgsqlConnection(connectionString);
        await kilitBaglantisi.OpenAsync(cancellationToken);

        await using (var kilitle = new NpgsqlCommand("SELECT pg_advisory_lock(20260826)", kilitBaglantisi))
            await kilitle.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            await islem();
        }
        finally
        {
            // Bağlantı kapanınca oturum kilitleri zaten düşer; bu açık bırakma
            // yalnız niyeti okunur kılıyor.
            await using var coz = new NpgsqlCommand("SELECT pg_advisory_unlock(20260826)", kilitBaglantisi);
            await coz.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
```

Dosyanın başına ekle: `using Npgsql;`

- [ ] **Adım 2: `Program.cs`'e kancayı ekle**

`src/Poyra.Api/Program.cs` içinde şu satırı bul:

```csharp
await DatabaseRoleGuard.EnsureNotPrivilegedAsync(connectionString, app.Environment, app.Logger);
```

ALTINA ekle:

```csharp
// Demo verisi: yalnız bayrak açıkken ve veritabanı BOŞKEN. Kilit, birden çok kopya
// aynı anda kalkarsa yalnız birinin tohumlamasını sağlar. Hata çıkarsa açılış sürer.
var demoAyarlari = app.Configuration.GetSection(DemoSeedOptions.Section).Get<DemoSeedOptions>()
                   ?? new DemoSeedOptions();

if (demoAyarlari.Enabled)
{
    await using var demoKapsami = app.Services.CreateAsyncScope();
    await DemoDataWriter.KilitliKosAsync(connectionString, async () =>
        await DemoDataSeeder.SeedAsync(
            demoAyarlari,
            ct => DemoDataWriter.IsyeriVarMiAsync(demoKapsami.ServiceProvider, ct),
            ct => DemoDataWriter.YazAsync(demoKapsami.ServiceProvider, demoAyarlari, app.Logger, ct),
            app.Logger),
        CancellationToken.None);
}
```

- [ ] **Adım 3: Derlendiğini ve mevcut testlerin bozulmadığını doğrula**

Çalıştır: `./scripts/test-hizli.sh`

Beklenen: iki proje de `Başarısız: 0`.

- [ ] **Adım 4: Compose dosyalarına değişkenleri ekle — YALNIZ `api` servisine**

`docker-compose.dokploy.yml` içinde `api` servisinin `environment` bloğunu şöyle yap:

```yaml
    environment:
      <<: *app-env
      Database__AutoMigrate: "false"    # şemayı migrate işi kurar
      # Demo verisi yalnız API'yi ilgilendirir: panel ve checkout demo parolasını görmez.
      Poyra__Demo__Enabled: ${POYRA_DEMO:-false}
      Poyra__Demo__Email: ${POYRA_DEMO_EMAIL:-}
      Poyra__Demo__Password: ${POYRA_DEMO_PASSWORD:-}
```

`docker-compose.prod.yml` içinde `api` servisinin `environment` bloğuna aynı üç satırı ekle.

- [ ] **Adım 5: Compose'ların geçerli olduğunu doğrula**

```bash
docker compose -f docker-compose.dokploy.yml config -q && docker compose -f docker-compose.prod.yml config -q && echo GECERLI
```

Beklenen: `GECERLI` (gerekli değişkenleri taşıyan bir `--env-file` ile koşun).

- [ ] **Adım 6: Commit**

```bash
git add src/Poyra.Api/Program.cs src/Poyra.Api/Database/DemoDataWriter.cs docker-compose.dokploy.yml docker-compose.prod.yml
git commit -m "feat(api): demo tohumlamasını açılışa bağla"
```

---

### Görev 4: Müşteriler ve ödemeler

**Dosyalar:**
- Değiştir: `src/Poyra.Api/Database/DemoDataWriter.cs`
- Değiştir: `tests/Poyra.Tests.Integration/DemoSeedTests.cs`

**Arayüzler:**
- Tüketir: Görev 2'den `DemoDataWriter.YazAsync`.
- Üretir: `YazAsync` artık müşteri ve ödeme de yazar. Görev 5 aynı metodu genişletir.

Pano grafikleri düz çizgi olmasın diye ödemeler son 30 güne yayılır ve durumları karışıktır.

- [ ] **Adım 1: Başarısız testi ekle**

`tests/Poyra.Tests.Integration/DemoSeedTests.cs` içine ekle:

```csharp
    [Fact]
    public async Task Musteri_ve_farkli_durumlarda_odeme_yazmali()
    {
        await using var kapsam = fixture.CreateApiScope();
        var ayarlar = Ayarlar();

        await DemoDataWriter.YazAsync(
            kapsam.ServiceProvider, ayarlar, NullLogger.Instance, TestContext.Current.CancellationToken);

        var tenancy = kapsam.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var isyeriId = await tenancy.Tenants
            .Where(t => t.Slug == ayarlar.TenantSlug)
            .Select(t => t.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var musteriler = fixture.CreateCustomers(PostgresFixture.TenantCtx(isyeriId));
        (await musteriler.Customers.CountAsync(TestContext.Current.CancellationToken))
            .ShouldBeGreaterThanOrEqualTo(4);

        var odemeler = fixture.CreatePayments(PostgresFixture.TenantCtx(isyeriId));
        var hepsi = await odemeler.PaymentIntents.ToListAsync(TestContext.Current.CancellationToken);

        hepsi.Count.ShouldBeGreaterThanOrEqualTo(20);
        hepsi.Select(x => x.Status).Distinct().Count().ShouldBeGreaterThanOrEqualTo(2);
        hepsi.ShouldContain(x => x.Status == PaymentStatus.Succeeded);
        hepsi.ShouldContain(x => x.Status == PaymentStatus.Failed);

        // Son 30 güne yayılmış olmalı: pano grafiği tek güne yığılmasın.
        hepsi.Select(x => x.CreatedAt.Date).Distinct().Count().ShouldBeGreaterThanOrEqualTo(5);
    }
```

Dosyanın başına ekle: `using Poyra.Modules.Payments.Domain;`

- [ ] **Adım 2: Testi koş, başarısız olduğunu doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~Musteri_ve_farkli"`

Beklenen: `Shouldly.ShouldAssertException` — müşteri sayısı 0, beklenen ≥ 4.

- [ ] **Adım 3: Müşteri ve ödeme yazımını ekle**

`src/Poyra.Api/Database/DemoDataWriter.cs` içinde `YazAsync`'in sonuna, günlük satırından
ÖNCE ekle:

```csharp
        var tenantContext = services.GetRequiredService<TenantContext>();
        tenantContext.Set(isyeri.TenantId);

        await MusteriVeOdemeYazAsync(services, isyeri.TenantId, isyeri.ProfileId, cancellationToken);
```

Ve sınıfa şu metodu ekle:

```csharp
    /// <summary>
    /// Demo müşterileri ve son 30 güne yayılmış ödemeler. Tutarlar ve tarihler
    /// SABİTTİR (Random yok): demo ekran görüntüleri dağıtımlar arasında değişmesin.
    /// </summary>
    private static async Task MusteriVeOdemeYazAsync(
        IServiceProvider services, Guid tenantId, Guid profileId, CancellationToken cancellationToken)
    {
        var musteriDb = services.GetRequiredService<CustomersDbContext>();
        var odemeDb = services.GetRequiredService<PaymentsDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var bugun = clock.UtcNow;

        string[,] kisiler =
        {
            { "mus-001", "Ayşe Yılmaz",    "ayse@ornek.test",   "+905321112233" },
            { "mus-002", "Mehmet Demir",   "mehmet@ornek.test", "+905332223344" },
            { "mus-003", "Zeynep Kaya",    "zeynep@ornek.test", "+905343334455" },
            { "mus-004", "Emre Şahin",     "emre@ornek.test",   "+905354445566" },
            { "mus-005", "Elif Çelik",     "elif@ornek.test",   "+905365556677" },
        };

        for (var i = 0; i < kisiler.GetLength(0); i++)
        {
            musteriDb.Customers.Add(new Customer
            {
                TenantId = tenantId,
                Ref = kisiler[i, 0],
                Name = kisiler[i, 1],
                Email = kisiler[i, 2],
                Phone = kisiler[i, 3],
            });
        }

        await musteriDb.SaveChangesAsync(cancellationToken);

        // 24 ödeme: 6'sı başarısız, kalanı başarılı. Tutarlar 149,90 TL'den başlayıp artar.
        for (var i = 0; i < 24; i++)
        {
            var tutar = 14990 + (i * 3175);
            var odeme = PaymentIntent.Create(
                tenantId,
                profileId,
                Money.Of(tutar, "TRY"),
                $"Demo sipariş #{1000 + i}",
                installments: i % 4 == 0 ? 3 : 1,
                customerRef: kisiler[i % kisiler.GetLength(0), 0],
                channel: "api");

            if (i % 4 == 1)
                odeme.MarkFailed();
            else
                odeme.MarkSucceededDirect();

            odemeDb.PaymentIntents.Add(odeme);
        }

        await odemeDb.SaveChangesAsync(cancellationToken);

        // Tarihleri geriye yay: CreatedAt denetim yorumlayıcısı tarafından yazıldığı için
        // kayıt SONRASI güncellenir. 24 ödeme 30 güne dağılır.
        var yazilanlar = await odemeDb.PaymentIntents
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        for (var i = 0; i < yazilanlar.Count; i++)
        {
            var gunOnce = 29 - (i * 29 / Math.Max(1, yazilanlar.Count - 1));
            yazilanlar[i].CreatedAt = bugun.AddDays(-gunOnce);
            yazilanlar[i].UpdatedAt = yazilanlar[i].CreatedAt;
        }

        await odemeDb.SaveChangesAsync(cancellationToken);
    }
```

Dosyanın başına ekle:

```csharp
using Poyra.Modules.Customers;
using Poyra.Modules.Customers.Domain;
using Poyra.Modules.Payments;
using Poyra.Modules.Payments.Domain;
using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;
```

`PostgresFixture.CreateApiScope()` içine, `services.AddScoped(_ => CreateTenancy(...))`
satırının altına ekle:

```csharp
        services.AddScoped(sp => CreateCustomers(sp.GetRequiredService<TenantContext>()));
        services.AddScoped(sp => CreatePayments(sp.GetRequiredService<TenantContext>()));
        services.AddSingleton<IClock, SystemClock>();
```

- [ ] **Adım 4: Testi koş, geçtiğini doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~DemoSeedTests"`

Beklenen: `Başarılı! - Başarısız: 0, Başarılı: 3`

- [ ] **Adım 5: Commit**

```bash
git add src/Poyra.Api/Database/DemoDataWriter.cs tests/Poyra.Tests.Integration/DemoSeedTests.cs tests/Poyra.Tests.Integration/PostgresFixture.cs
git commit -m "feat(api): demo müşteri ve ödeme verisi"
```

---

### Görev 5: POS bağlantısı, rota kuralı, ödeme linki ve webhook

**Dosyalar:**
- Değiştir: `src/Poyra.Api/Database/DemoDataWriter.cs`
- Değiştir: `tests/Poyra.Tests.Integration/DemoSeedTests.cs`

**Arayüzler:**
- Tüketir: Görev 4'ten `YazAsync`.
- Üretir: Yeni tür yok; yalnız satır ekler.

`ConnectorAccount.CredentialsEncrypted` boş bırakılır ve `TestMode = true` olur — demo
kurulumunda gerçek banka kimliği yoktur, bağlantı yalnız panelde görünsün diye vardır.

- [ ] **Adım 1: Başarısız testi ekle**

`tests/Poyra.Tests.Integration/DemoSeedTests.cs` içine ekle:

```csharp
    [Fact]
    public async Task Pos_baglantisi_rota_kurali_odeme_linki_ve_webhook_yazmali()
    {
        await using var kapsam = fixture.CreateApiScope();
        var ayarlar = Ayarlar();

        await DemoDataWriter.YazAsync(
            kapsam.ServiceProvider, ayarlar, NullLogger.Instance, TestContext.Current.CancellationToken);

        var isyeriId = await kapsam.ServiceProvider.GetRequiredService<TenancyDbContext>()
            .Tenants.Where(t => t.Slug == ayarlar.TenantSlug).Select(t => t.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var ctx = PostgresFixture.TenantCtx(isyeriId);

        (await fixture.CreateConnectors(ctx).ConnectorAccounts
            .CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        var kural = await fixture.CreateRouting(ctx).RoutingRules
            .SingleAsync(TestContext.Current.CancellationToken);
        kural.IsActive.ShouldBeTrue();
        kural.Document.ShouldNotBeNullOrWhiteSpace();

        (await fixture.CreatePaymentLinks(ctx).PaymentLinks
            .CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        var uc = await fixture.CreateWebhooks(ctx).WebhookEndpoints
            .SingleAsync(TestContext.Current.CancellationToken);
        uc.Active.ShouldBeTrue();
        uc.EventTypes.ShouldNotBeEmpty();
    }
```

- [ ] **Adım 2: Testi koş, başarısız olduğunu doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~Pos_baglantisi"`

Beklenen: `error CS1061: 'PostgresFixture' 'CreatePaymentLinks' tanımı içermiyor`
(fikstürde `CreateWebhooks` ve `CreatePaymentLinks` yok).

- [ ] **Adım 3: Fikstüre eksik iki bağlamı ekle**

`tests/Poyra.Tests.Integration/PostgresFixture.cs` içine, diğer `Create*` metodlarının yanına:

```csharp
    public Poyra.Modules.Webhooks.WebhooksDbContext CreateWebhooks(TenantContext tenant)
        => new(PoyraDb.BuildOptions<Poyra.Modules.Webhooks.WebhooksDbContext>(
            AppCs, Poyra.Modules.Webhooks.WebhooksDbContext.MigrationsHistoryTable, tenant, _clock), tenant);

    public Poyra.Modules.PaymentLinks.PaymentLinksDbContext CreatePaymentLinks(TenantContext tenant)
        => new(PoyraDb.BuildOptions<Poyra.Modules.PaymentLinks.PaymentLinksDbContext>(
            AppCs, Poyra.Modules.PaymentLinks.PaymentLinksDbContext.MigrationsHistoryTable, tenant, _clock), tenant);
```

Bu, dosyadaki mevcut `CreateTenancy` / `CreatePayments` deseniyle birebir aynıdır.

- [ ] **Adım 4: Kalan satırları yaz**

`src/Poyra.Api/Database/DemoDataWriter.cs` içinde `MusteriVeOdemeYazAsync` çağrısının
ALTINA ekle:

```csharp
        await BaglantiVeKurallariYazAsync(services, isyeri.TenantId, cancellationToken);
```

Ve sınıfa ekle:

```csharp
    /// <summary>
    /// POS bağlantısı, rota kuralı, ödeme linki ve webhook ucu — panelin ilgili
    /// ekranları boş açılmasın diye. Bağlantıda gerçek banka kimliği YOKTUR.
    /// </summary>
    private static async Task BaglantiVeKurallariYazAsync(
        IServiceProvider services, Guid tenantId, CancellationToken cancellationToken)
    {
        var baglantiDb = services.GetRequiredService<ConnectorsDbContext>();
        baglantiDb.ConnectorAccounts.Add(new ConnectorAccount
        {
            TenantId = tenantId,
            ConnectorKey = NestPayConnector.ConnectorKey,
            Label = "Demo POS (test)",
            CredentialsEncrypted = [],   // demo: gerçek kimlik yok
            TestMode = true,
            Priority = 100,
        });
        await baglantiDb.SaveChangesAsync(cancellationToken);

        var rotaDb = services.GetRequiredService<RoutingDbContext>();
        rotaDb.RoutingRules.Add(new RoutingRule
        {
            TenantId = tenantId,
            Name = "Demo rota",
            IsActive = true,
            Document = """
                {"version":2,"rules":[{"name":"Varsayılan","when":{},"then":{"strategy":"priority"}}]}
                """,
        });
        await rotaDb.SaveChangesAsync(cancellationToken);

        var linkDb = services.GetRequiredService<PaymentLinksDbContext>();
        linkDb.PaymentLinks.Add(new PaymentLink
        {
            TenantId = tenantId,
            Slug = "demo-urun",
            Description = "Demo ürün — tek çekim",
            AmountMinor = 49900,
            MaxInstallments = 3,
            MaxUsage = 0,
        });
        await linkDb.SaveChangesAsync(cancellationToken);

        var webhookDb = services.GetRequiredService<WebhooksDbContext>();
        webhookDb.WebhookEndpoints.Add(new WebhookEndpoint
        {
            TenantId = tenantId,
            Url = "https://ornek.test/poyra/webhook",
            EventTypes = ["payment.succeeded", "payment.failed"],
            SecretEncrypted = [],
            Active = true,
        });
        await webhookDb.SaveChangesAsync(cancellationToken);
    }
```

Dosyanın başına ekle:

```csharp
using Poyra.Connectors.NestPay;
using Poyra.Modules.Connectors;
using Poyra.Modules.Connectors.Domain;
using Poyra.Modules.PaymentLinks;
using Poyra.Modules.PaymentLinks.Domain;
using Poyra.Modules.Routing;
using Poyra.Modules.Routing.Domain;
using Poyra.Modules.Webhooks;
using Poyra.Modules.Webhooks.Domain;
```

`PostgresFixture.CreateApiScope()` içine dört bağlamı daha kaydet:

```csharp
        services.AddScoped(sp => CreateConnectors(sp.GetRequiredService<TenantContext>()));
        services.AddScoped(sp => CreateRouting(sp.GetRequiredService<TenantContext>()));
        services.AddScoped(sp => CreateWebhooks(sp.GetRequiredService<TenantContext>()));
        services.AddScoped(sp => CreatePaymentLinks(sp.GetRequiredService<TenantContext>()));
```

- [ ] **Adım 5: Testleri koş, geçtiğini doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~DemoSeedTests"`

Beklenen: `Başarılı! - Başarısız: 0, Başarılı: 4`

- [ ] **Adım 6: Tüm süiti koş**

Çalıştır: `./scripts/test-hizli.sh && dotnet test tests/Poyra.Tests.Integration`

Beklenen: hepsinde `Başarısız: 0`.

- [ ] **Adım 7: README'ye demo bölümünü ekle**

`README.md` içinde "Üretim kurulumu (self-host)" bölümündeki madde listesinin sonuna ekle:

```markdown
- **Demo verisi:** `POYRA_DEMO=true` ve veritabanı BOŞSA açılışta örnek veri kurulur —
  işyeri, `POYRA_DEMO_EMAIL`/`POYRA_DEMO_PASSWORD` ile giriş yapan kullanıcı, müşteriler,
  son 30 güne yayılmış ödemeler, bir POS bağlantısı, rota kuralı, ödeme linki ve webhook.
  Bir tane bile işyeri varsa hiçbir şey yazılmaz; bayrak açık unutulsa da gerçek bir
  kurulum kirlenmez. Demoyu tazelemek = veritabanı birimini silip yeniden dağıtmak.
```

- [ ] **Adım 8: Commit**

```bash
git add src/Poyra.Api/Database/DemoDataWriter.cs tests/Poyra.Tests.Integration/DemoSeedTests.cs tests/Poyra.Tests.Integration/PostgresFixture.cs README.md
git commit -m "feat(api): demo POS bağlantısı, rota kuralı, ödeme linki ve webhook"
```

---

## Bilinen Belirsizlikler

Uygulayıcı bunlarla karşılaşırsa duraklamasın, koddaki gerçeğe uysun:

1. **Rota kuralı `Document` şeması.** Örnek JSON, motorun v2 şemasına uymayabilir.
   `src/Modules/Poyra.Modules.Routing/Contracts/RoutingContracts.cs` içindeki gerçek
   sözleşme esas alınır; kural panelde görünsün diye vardır, çalıştırılması beklenmez.
2. **`PaymentIntent.CreatedAt` geri alma.** Denetim yorumlayıcısı `SaveChanges` sırasında
   tarihi yazar; plan bu yüzden ikinci bir `SaveChanges` ile geri yayıyor. Yorumlayıcı
   ikinci kayıtta tarihi yeniden ezerse, ödemeler doğrudan SQL ile güncellenir.
3. **`Poyra.Api`'nin `Poyra.Connectors.NestPay`'e referansı.** Yoksa
   `ConnectorKey` sabiti yerine DI'daki `IPaymentConnector` kayıtlarından ilkinin
   `Key` değeri kullanılır — demo bağlantısı hangi konnektör olduğuna bakmaz.
