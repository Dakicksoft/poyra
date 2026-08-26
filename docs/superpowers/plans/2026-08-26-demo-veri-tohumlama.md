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

- **Adlandırma:** metod adları, değişken adları, parametreler ve enum değerleri
  **İngilizce**. Yorumlar, XML belgeleri, günlük mesajları ve demo içeriği (müşteri
  adları, açıklamalar) **Türkçe**.
- **Tek istisna — test ADLARI Türkçe kalır:** davranış açıklaması işlevi görürler ve
  repodaki 795 birim testi bu düzende. Test gövdeleri ve yardımcıları İngilizce'dir.
  Örnek: `public async Task Bayrak_kapaliyken_hic_dokunmamali()` içinde `var touched = false;`.
- Birim testleri **Docker istemez**; gerçek Postgres gerektiren her şey entegrasyon projesine gider.
- Commit mesajları Conventional Commits + Türkçe: `feat(api): …`, `test(api): …`.
- **Co-Authored-By satırı EKLENMEZ.**
- Doğrudan `develop` dalına commit edilir. Push kullanıcı onayı ister — plan hiçbir adımda push etmez.
- Hiçbir görev veri SİLMEZ. `DELETE`, `ExecuteDelete`, `RemoveRange` bu planda yasaktır.
- Tohumlayıcı asla açılışı düşürmez: her hata yakalanır, uyarı olarak günlüğe yazılır.
- Uygulama veritabanına `poyra_app` rolüyle bağlanır (RLS'e tabi). Tohumlama da bu rolle
  yazar — ayrıcalıklı bir yola sapılmaz.
- **`Poyra.Tests.Unit` projesine `Poyra.Api` referansı EKLENMEZ.** Test projesi zaten
  `Poyra.Panel`'e bağlı; ikinci bir host projesi eklenince iki `appsettings.json` aynı
  çıktı dosyasına kopyalanmaya çalışıyor ve derleme MSB3021 ile kırılıyor. Bu yüzden
  karar mantığı (`DemoSeedOptions`, `DemoSeedOutcome`, `DemoDataSeeder`) modül bağımlılığı
  olmayan `Poyra.Persistence`'ta durur; modülleri tanıyan `DemoDataWriter` `Poyra.Api`'de
  kalır ve yalnız entegrasyon testinden çağrılır. Aynı sebeple `DatabaseRoleGuard` da
  bu oturumda `Poyra.Persistence`'a taşındı.

## Dosya Yapısı

| Dosya | Sorumluluk |
|---|---|
| `src/Poyra.Persistence/DemoSeedOptions.cs` (yeni) | Ayar kaydı + sonuç enum'u. Bağımlılığı yok. |
| `src/Poyra.Persistence/DemoDataSeeder.cs` (yeni) | Karar mantığı + kilit + sıralama. Veri yazmaz. |
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
- Oluştur: `src/Poyra.Persistence/DemoSeedOptions.cs`
- Oluştur: `src/Poyra.Persistence/DemoDataSeeder.cs`
- Test: `tests/Poyra.Tests.Unit/DemoDataSeederTests.cs`
- Değiştir: `.env.example`
- Değiştir: `scripts/anahtar-uret.sh`

**Arayüzler:**
- Üretir: `DemoSeedOptions` (kayıt), `DemoSeedOutcome` (enum),
  `DemoDataSeeder.SeedAsync(DemoSeedOptions, Func<CancellationToken,Task<bool>>, Func<CancellationToken,Task>, ILogger, CancellationToken) → Task<DemoSeedOutcome>`.
  Görev 2 ve 3 bu imzayı kullanır.

- [x] **Adım 1: Başarısız testi yaz**

`tests/Poyra.Tests.Unit/DemoDataSeederTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Poyra.Persistence;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

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
```

- [x] **Adım 2: Testi koş, başarısız olduğunu doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Unit --filter "FullyQualifiedName~DemoDataSeederTests"`

Beklenen: `error CS0246: 'DemoSeedOptions' türü bulunamadı` (tür henüz yok).

- [x] **Adım 3: Ayar kaydını yaz**

`src/Poyra.Persistence/DemoSeedOptions.cs`:

```csharp
namespace Poyra.Persistence;

/// <summary>Demo tohumlamasının sonucu — günlüğe ve testlere aynı kelimeyle döner.</summary>
public enum DemoSeedOutcome
{
    /// <summary>Bayrak kapalı; hiç bakılmadı.</summary>
    Disabled,

    /// <summary>Bayrak açık ama e-posta ya da parola verilmemiş.</summary>
    MissingSettings,

    /// <summary>Veritabanında en az bir işyeri var; hiçbir şey yazılmadı.</summary>
    TenantExists,

    /// <summary>Demo verisi kuruldu.</summary>
    Seeded,

    /// <summary>Tohumlama sırasında hata çıktı; açılış sürdürüldü.</summary>
    Failed,
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

- [x] **Adım 4: Karar mantığını yaz**

`src/Poyra.Persistence/DemoDataSeeder.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Poyra.Persistence;

/// <summary>
/// Demo verisinin KURULUP kurulmayacağına karar verir; satırları kendisi yazmaz
/// (onu DemoDataWriter yapar). Bu ayrım sayesinde kararlar veritabanı olmadan sınanır.
/// </summary>
public static class DemoDataSeeder
{
    public static async Task<DemoSeedOutcome> SeedAsync(
        DemoSeedOptions options,
        Func<CancellationToken, Task<bool>> tenantExists,
        Func<CancellationToken, Task> seed,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
            return DemoSeedOutcome.Disabled;

        if (string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrWhiteSpace(options.Password))
        {
            logger.LogWarning(
                "Demo tohumlaması açık ama {Section}:Email / :Password verilmemiş — atlanıyor.",
                DemoSeedOptions.Section);
            return DemoSeedOutcome.MissingSettings;
        }

        try
        {
            // Bir tane bile işyeri varsa burası gerçek bir kurulumdur: dokunulmaz.
            if (await tenantExists(cancellationToken))
            {
                logger.LogInformation(
                    "Demo tohumlaması atlandı: veritabanında zaten işyeri var.");
                return DemoSeedOutcome.TenantExists;
            }

            await seed(cancellationToken);
            logger.LogInformation("Demo verisi kuruldu (giriş: {Email}).", options.Email);
            return DemoSeedOutcome.Seeded;
        }
        catch (Exception exception)
        {
            // Demo verisi açılışı düşürmeye değmez: uygulama demo verisi olmadan da çalışır.
            logger.LogWarning(exception, "Demo tohumlaması başarısız — açılış sürdürülüyor.");
            return DemoSeedOutcome.Failed;
        }
    }
}
```

- [x] **Adım 5: Testleri koş, geçtiğini doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Unit --filter "FullyQualifiedName~DemoDataSeederTests"`

Beklenen: `Başarılı! - Başarısız: 0, Başarılı: 8`

- [x] **Adım 6: `.env.example`'a optionsı ekle**

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

- [x] **Adım 7: `scripts/anahtar-uret.sh`'e demo parolasını ekle**

Dosyanın SONUNA ekle:

```sh
# Demo giriş parolası — yalnız POYRA_DEMO=true iken kullanılır.
echo "POYRA_DEMO_PASSWORD=$(openssl rand -base64 18 | tr -d '/+=')"
```

- [x] **Adım 8: Betiğin çalıştığını doğrula**

Çalıştır: `./scripts/anahtar-uret.sh | grep POYRA_DEMO_PASSWORD`

Beklenen: `POYRA_DEMO_PASSWORD=` sonrası ~24 karakterlik alfanümerik dizi.

- [x] **Adım 9: Commit**

```bash
git add src/Poyra.Persistence/DemoSeedOptions.cs src/Poyra.Persistence/DemoDataSeeder.cs tests/Poyra.Tests.Unit/DemoDataSeederTests.cs .env.example scripts/anahtar-uret.sh
git commit -m "feat(api): demo tohumlama optionsı ve karar mantığı"
```

---

### Görev 2: İşyeri ve giriş kullanıcısı

**Dosyalar:**
- Oluştur: `src/Poyra.Api/Database/DemoDataWriter.cs`
- Test: `tests/Poyra.Tests.Integration/DemoSeedTests.cs`

**Arayüzler:**
- Tüketir: Görev 1'den `DemoSeedOptions`, `DemoSeedOutcome`, `DemoDataSeeder.SeedAsync`.
- Üretir: `DemoDataWriter.TenantExistsAsync(IServiceProvider, CancellationToken) → Task<bool>`
  ve `DemoDataWriter.WriteAsync(IServiceProvider, DemoSeedOptions, ILogger, CancellationToken) → Task`.
  Görev 3 (`Program.cs` kancası) bu iki imzayı çağırır; Görev 4 ve 5 `WriteAsync` gövdesini büyütür.

Mevcut `CreateTenantCommand` kullanılır — organizasyon, işyeri, varsayılan iş profili,
API anahtarı ve parolası hash'lenmiş sahip kullanıcıyı birlikte kurar. Elle `User` üretip
parola hash'lemek bu doğrulanmış yolu atlamak olurdu.

- [x] **Adım 1: Başarısız entegrasyon testini yaz**

`tests/Poyra.Tests.Integration/DemoSeedTests.cs`:

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Poyra.Api.Database;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Tenancy.Domain;
using Poyra.Persistence;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

[Collection(nameof(PostgresFixture))]
public sealed class DemoSeedTests(PostgresFixture fixture)
{
    private static DemoSeedOptions Options() => new()
    {
        Enabled = true,
        Email = "demo@poyra.test",
        Password = "cok-uzun-demo-parolasi",
        TenantSlug = $"demo-{Guid.CreateVersion7():N}"[..20],
    };

    [Fact]
    public async Task Bos_veritabanina_isyeri_ve_giris_usersi_kurmali()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var options = Options();

        await DemoDataWriter.WriteAsync(
            scope.ServiceProvider, options, NullLogger.Instance, TestContext.Current.CancellationToken);

        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = await db.Tenants.SingleOrDefaultAsync(
            t => t.Slug == options.TenantSlug, TestContext.Current.CancellationToken);
        tenant.ShouldNotBeNull();

        var user = await db.Users.SingleOrDefaultAsync(
            u => u.Email == options.Email, TestContext.Current.CancellationToken);
        user.ShouldNotBeNull();
        user.PasswordHash.ShouldNotBeNullOrWhiteSpace();
        user.PasswordHash.ShouldNotBe(options.Password); // düz metin saklanmamalı

        // Parola gerçekten ÇALIŞMALI: hash doğrulanabiliyor mu?
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        hasher.VerifyHashedPassword(user, user.PasswordHash, options.Password!)
            .ShouldNotBe(PasswordVerificationResult.Failed);
    }

    [Fact]
    public async Task Isyeri_varken_tohumlayici_hicbir_sey_yazmamali()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var options = Options();

        var beforeCount = await scope.ServiceProvider
            .GetRequiredService<TenancyDbContext>().Tenants
            .CountAsync(TestContext.Current.CancellationToken);

        var result = await DemoDataSeeder.SeedAsync(
            options,
            ct => DemoDataWriter.TenantExistsAsync(scope.ServiceProvider, ct),
            ct => DemoDataWriter.WriteAsync(scope.ServiceProvider, options, NullLogger.Instance, ct),
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        result.ShouldBe(DemoSeedOutcome.TenantExists);

        var afterCount = await scope.ServiceProvider
            .GetRequiredService<TenancyDbContext>().Tenants
            .CountAsync(TestContext.Current.CancellationToken);
        afterCount.ShouldBe(beforeCount);
    }
}
```

**UYGULAMADA DEĞİŞTİ.** Fikstüre elle `ServiceCollection` kurmak yerine depoda zaten
kullanılan `WebApplicationFactory<ApiEntryPoint>` deseni benimsendi (bkz.
`CustomerFlowTests`): tüm DbContext'ler, `IDispatcher` ve `IPasswordHasher<User>` hazır
gelir, elle kayıt gerekmez. Kapsam şöyle alınır:

```csharp
await using var scope = _factory.Services.CreateAsyncScope();
```

Fabrika `UseEnvironment("Testing")` ile kurulur; böylece `DatabaseRoleGuard` üretim
davranışına girmez. Sonraki görevler de bu kapsamı kullanır — `PostgresFixture`'a
`CreateApiScope()` EKLENMEZ.

- [x] **Adım 4: Testi koş — bu sefer yazıcı eksik olmalı**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~DemoSeedTests"`

Beklenen: `error CS0103: 'DemoDataWriter' adı geçerli değil`.

- [x] **Adım 5: Yazıcıyı yaz**

`src/Poyra.Api/Database/DemoDataWriter.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Tenancy.Features.CreateTenant;
using Poyra.Persistence;
using Poyra.SharedKernel.Cqrs;

namespace Poyra.Api.Database;

/// <summary>
/// Demo satırlarını yazar. Hiçbir metodu veri SİLMEZ; tohumlayıcı zaten yalnız boş
/// veritabanında çağırır.
/// </summary>
public static class DemoDataWriter
{
    public static async Task<bool> TenantExistsAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TenancyDbContext>();
        return await db.Tenants.AnyAsync(cancellationToken);
    }

    public static async Task WriteAsync(
        IServiceProvider services,
        DemoSeedOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var dispatcher = services.GetRequiredService<IDispatcher>();

        // Mevcut ve doğrulanmış yol: organizasyon + işyeri + varsayılan profil +
        // API anahtarı + parolası hash'lenmiş sahip kullanıcı birlikte kurulur.
        var tenant = await dispatcher.Send(
            new CreateTenantCommand(
                options.TenantName,
                options.TenantSlug,
                options.Email,
                options.Password,
                options.OwnerName),
            cancellationToken);

        logger.LogInformation(
            "Demo işyeri kuruldu: {Slug} ({TenantId}).", tenant.Slug, tenant.TenantId);
    }
}
```

- [x] **Adım 6: Testleri koş, geçtiğini doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~DemoSeedTests"`

Beklenen: `Başarılı! - Başarısız: 0, Başarılı: 2`

- [x] **Adım 7: Commit**

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
- Üretir: `DemoDataWriter.RunLockedAsync(string connectionString, Func<Task> is, CancellationToken) → Task`.

- [x] **Adım 1: Kilit yardımcısını yaz**

`src/Poyra.Api/Database/DemoDataWriter.cs` içine, sınıfın sonuna ekle:

```csharp
    /// <summary>
    /// Verilen işi oturum düzeyi advisory lock altında koşturur.
    ///
    /// Her modülün kendi DbContext'i (dolayısıyla kendi bağlantısı) var; tek transaction
    /// allni kapsayamaz. Bu yüzden kilit, tohumlama boyunca AÇIK TUTULAN ayrı bir
    /// bağlantıda alınır. Böylece iki API kopyası aynı anda kalksa bile "işyeri var mı?"
    /// kontrolü ile yazma arasına başka kimse giremez.
    /// </summary>
    public static async Task RunLockedAsync(
        string connectionString, Func<Task> work, CancellationToken cancellationToken)
    {
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken);

        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(20260826)", lockConnection))
            await acquire.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            await work();
        }
        finally
        {
            // Bağlantı kapanınca oturum kilitleri zaten düşer; bu açık bırakma
            // yalnız niyeti okunur kılıyor.
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(20260826)", lockConnection);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
```

Dosyanın başına ekle: `using Npgsql;`

- [x] **Adım 2: `Program.cs`'e kancayı ekle**

`src/Poyra.Api/Program.cs` içinde şu satırı bul:

```csharp
await DatabaseRoleGuard.EnsureNotPrivilegedAsync(connectionString, app.Environment, app.Logger);
```

ALTINA ekle:

```csharp
// Demo verisi: yalnız bayrak açıkken ve veritabanı BOŞKEN. Kilit, birden çok kopya
// aynı anda kalkarsa yalnız birinin tohumlamasını sağlar. Hata çıkarsa açılış sürer.
var demoOptions = app.Configuration.GetSection(DemoSeedOptions.Section).Get<DemoSeedOptions>()
                   ?? new DemoSeedOptions();

if (demoOptions.Enabled)
{
    await using var demoScope = app.Services.CreateAsyncScope();
    await DemoDataWriter.RunLockedAsync(connectionString, async () =>
        await DemoDataSeeder.SeedAsync(
            demoOptions,
            ct => DemoDataWriter.TenantExistsAsync(demoScope.ServiceProvider, ct),
            ct => DemoDataWriter.WriteAsync(demoScope.ServiceProvider, demoOptions, app.Logger, ct),
            app.Logger),
        CancellationToken.None);
}
```

- [x] **Adım 3: Derlendiğini ve mevcut testlerin bozulmadığını doğrula**

Çalıştır: `./scripts/test-hizli.sh`

Beklenen: iki proje de `Başarısız: 0`.

- [x] **Adım 4: Compose dosyalarına değişkenleri ekle — YALNIZ `api` servisine**

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

- [x] **Adım 5: Compose'ların geçerli olduğunu doğrula**

```bash
docker compose -f docker-compose.dokploy.yml config -q && docker compose -f docker-compose.prod.yml config -q && echo GECERLI
```

Beklenen: `GECERLI` (gerekli değişkenleri taşıyan bir `--env-file` ile koşun).

- [x] **Adım 6: Commit**

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
- Tüketir: Görev 2'den `DemoDataWriter.WriteAsync`.
- Üretir: `WriteAsync` artık müşteri ve ödeme de yazar. Görev 5 aynı metodu genişletir.

Pano grafikleri düz çizgi olmasın diye ödemeler son 30 güne yayılır ve durumları karışıktır.

- [x] **Adım 1: Başarısız testi ekle**

`tests/Poyra.Tests.Integration/DemoSeedTests.cs` içine ekle:

```csharp
    [Fact]
    public async Task Musteri_ve_farkli_durumlarda_odeme_yazmali()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var options = Options();

        await DemoDataWriter.WriteAsync(
            scope.ServiceProvider, options, NullLogger.Instance, TestContext.Current.CancellationToken);

        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenantId = await tenancy.Tenants
            .Where(t => t.Slug == options.TenantSlug)
            .Select(t => t.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var customers = fixture.CreateCustomers(PostgresFixture.TenantCtx(tenantId));
        (await customers.Customers.CountAsync(TestContext.Current.CancellationToken))
            .ShouldBeGreaterThanOrEqualTo(4);

        var payments = fixture.CreatePayments(PostgresFixture.TenantCtx(tenantId));
        var all = await payments.PaymentIntents.ToListAsync(TestContext.Current.CancellationToken);

        all.Count.ShouldBeGreaterThanOrEqualTo(20);
        all.Select(x => x.Status).Distinct().Count().ShouldBeGreaterThanOrEqualTo(2);
        all.ShouldContain(x => x.Status == PaymentStatus.Succeeded);
        all.ShouldContain(x => x.Status == PaymentStatus.Failed);

        // Son 30 güne yayılmış olmalı: pano grafiği tek güne yığılmasın.
        all.Select(x => x.CreatedAt.Date).Distinct().Count().ShouldBeGreaterThanOrEqualTo(5);
    }
```

Dosyanın başına ekle: `using Poyra.Modules.Payments.Domain;`

- [x] **Adım 2: Testi koş, başarısız olduğunu doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~Musteri_ve_farkli"`

Beklenen: `Shouldly.ShouldAssertException` — müşteri sayısı 0, beklenen ≥ 4.

- [x] **Adım 3: Müşteri ve ödeme yazımını ekle**

`src/Poyra.Api/Database/DemoDataWriter.cs` içinde `WriteAsync`'in sonuna, günlük satırından
ÖNCE ekle:

```csharp
        var tenantContext = services.GetRequiredService<TenantContext>();
        tenantContext.Set(tenant.TenantId);

        await WriteCustomersAndPaymentsAsync(services, tenant.TenantId, tenant.ProfileId, cancellationToken);
```

Ve sınıfa şu metodu ekle:

```csharp
    /// <summary>
    /// Demo müşterileri ve son 30 güne yayılmış ödemeler. Tutarlar ve tarihler
    /// SABİTTİR (Random yok): demo ekran görüntüleri dağıtımlar arasında değişmesin.
    /// </summary>
    private static async Task WriteCustomersAndPaymentsAsync(
        IServiceProvider services, Guid tenantId, Guid profileId, CancellationToken cancellationToken)
    {
        var customersDb = services.GetRequiredService<CustomersDbContext>();
        var paymentsDb = services.GetRequiredService<PaymentsDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var today = clock.UtcNow;

        string[,] people =
        {
            { "mus-001", "Ayşe Yılmaz",    "ayse@ornek.test",   "+905321112233" },
            { "mus-002", "Mehmet Demir",   "mehmet@ornek.test", "+905332223344" },
            { "mus-003", "Zeynep Kaya",    "zeynep@ornek.test", "+905343334455" },
            { "mus-004", "Emre Şahin",     "emre@ornek.test",   "+905354445566" },
            { "mus-005", "Elif Çelik",     "elif@ornek.test",   "+905365556677" },
        };

        for (var i = 0; i < people.GetLength(0); i++)
        {
            customersDb.Customers.Add(new Customer
            {
                TenantId = tenantId,
                Ref = people[i, 0],
                Name = people[i, 1],
                Email = people[i, 2],
                Phone = people[i, 3],
            });
        }

        await customersDb.SaveChangesAsync(cancellationToken);

        // 24 ödeme: 6'sı başarısız, kalanı başarılı. Tutarlar 149,90 TL'den başlayıp artar.
        for (var i = 0; i < 24; i++)
        {
            var amountMinor = 14990 + (i * 3175);
            var payment = PaymentIntent.Create(
                tenantId,
                profileId,
                Money.Of(amountMinor, "TRY"),
                $"Demo sipariş #{1000 + i}",
                installments: i % 4 == 0 ? 3 : 1,
                customerRef: people[i % people.GetLength(0), 0],
                channel: "api");

            if (i % 4 == 1)
                payment.MarkFailed();
            else
                payment.MarkSucceededDirect();

            paymentsDb.PaymentIntents.Add(payment);
        }

        await paymentsDb.SaveChangesAsync(cancellationToken);

        // Tarihleri geriye yay: CreatedAt denetim yorumlayıcısı tarafından yazıldığı için
        // kayıt SONRASI güncellenir. 24 ödeme 30 güne dağılır.
        var written = await paymentsDb.PaymentIntents
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        for (var i = 0; i < written.Count; i++)
        {
            var daysAgo = 29 - (i * 29 / Math.Max(1, written.Count - 1));
            written[i].CreatedAt = today.AddDays(-daysAgo);
            written[i].UpdatedAt = written[i].CreatedAt;
        }

        await paymentsDb.SaveChangesAsync(cancellationToken);
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

DbContext kaydı GEREKMEZ: `WebApplicationFactory` kapsamı `CustomersDbContext`,
`PaymentsDbContext` ve `IClock`'u zaten sağlıyor.

- [x] **Adım 4: Testi koş, geçtiğini doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~DemoSeedTests"`

Beklenen: `Başarılı! - Başarısız: 0, Başarılı: 3`

- [x] **Adım 5: Commit**

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
- Tüketir: Görev 4'ten `WriteAsync`.
- Üretir: Yeni tür yok; yalnız satır ekler.

`ConnectorAccount.CredentialsEncrypted` boş bırakılır ve `TestMode = true` olur — demo
kurulumunda gerçek banka kimliği yoktur, bağlantı yalnız panelde görünsün diye vardır.

- [ ] **Adım 1: Başarısız testi ekle**

`tests/Poyra.Tests.Integration/DemoSeedTests.cs` içine ekle:

```csharp
    [Fact]
    public async Task Pos_baglantisi_rota_kurali_odeme_linki_ve_webhook_yazmali()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var options = Options();

        await DemoDataWriter.WriteAsync(
            scope.ServiceProvider, options, NullLogger.Instance, TestContext.Current.CancellationToken);

        var tenantId = await scope.ServiceProvider.GetRequiredService<TenancyDbContext>()
            .Tenants.Where(t => t.Slug == options.TenantSlug).Select(t => t.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var ctx = PostgresFixture.TenantCtx(tenantId);

        (await fixture.CreateConnectors(ctx).ConnectorAccounts
            .CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        var rule = await fixture.CreateRouting(ctx).RoutingRules
            .SingleAsync(TestContext.Current.CancellationToken);
        rule.IsActive.ShouldBeTrue();
        rule.Document.ShouldNotBeNullOrWhiteSpace();

        (await fixture.CreatePaymentLinks(ctx).PaymentLinks
            .CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        var endpoint = await fixture.CreateWebhooks(ctx).WebhookEndpoints
            .SingleAsync(TestContext.Current.CancellationToken);
        endpoint.Active.ShouldBeTrue();
        endpoint.EventTypes.ShouldNotBeEmpty();
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

`src/Poyra.Api/Database/DemoDataWriter.cs` içinde `WriteCustomersAndPaymentsAsync` çağrısının
ALTINA ekle:

```csharp
        await WriteConnectorAndRulesAsync(services, tenant.TenantId, cancellationToken);
```

Ve sınıfa ekle:

```csharp
    /// <summary>
    /// POS bağlantısı, rota kuralı, ödeme linki ve webhook ucu — panelin ilgili
    /// ekranları boş açılmasın diye. Bağlantıda gerçek banka kimliği YOKTUR.
    /// </summary>
    private static async Task WriteConnectorAndRulesAsync(
        IServiceProvider services, Guid tenantId, CancellationToken cancellationToken)
    {
        var connectorsDb = services.GetRequiredService<ConnectorsDbContext>();
        connectorsDb.ConnectorAccounts.Add(new ConnectorAccount
        {
            TenantId = tenantId,
            ConnectorKey = NestPayConnector.ConnectorKey,
            Label = "Demo POS (test)",
            CredentialsEncrypted = [],   // demo: gerçek kimlik yok
            TestMode = true,
            Priority = 100,
        });
        await connectorsDb.SaveChangesAsync(cancellationToken);

        var routingDb = services.GetRequiredService<RoutingDbContext>();
        routingDb.RoutingRules.Add(new RoutingRule
        {
            TenantId = tenantId,
            Name = "Demo rota",
            IsActive = true,
            Document = """
                {"version":2,"rules":[{"name":"Varsayılan","when":{},"then":{"strategy":"priority"}}]}
                """,
        });
        await routingDb.SaveChangesAsync(cancellationToken);

        var linksDb = services.GetRequiredService<PaymentLinksDbContext>();
        linksDb.PaymentLinks.Add(new PaymentLink
        {
            TenantId = tenantId,
            Slug = "demo-urun",
            Description = "Demo ürün — tek çekim",
            AmountMinor = 49900,
            MaxInstallments = 3,
            MaxUsage = 0,
        });
        await linksDb.SaveChangesAsync(cancellationToken);

        var webhooksDb = services.GetRequiredService<WebhooksDbContext>();
        webhooksDb.WebhookEndpoints.Add(new WebhookEndpoint
        {
            TenantId = tenantId,
            Url = "https://ornek.test/poyra/webhook",
            EventTypes = ["payment.succeeded", "payment.failed"],
            SecretEncrypted = [],
            Active = true,
        });
        await webhooksDb.SaveChangesAsync(cancellationToken);
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

DbContext kaydı GEREKMEZ: `WebApplicationFactory` kapsamı bu dört bağlamı da sağlıyor.
Fikstüre eklenen `CreateWebhooks` / `CreatePaymentLinks` yalnız DOĞRULAMA sorguları için.

- [ ] **Adım 5: Testleri koş, geçtiğini doğrula**

Çalıştır: `dotnet test tests/Poyra.Tests.Integration --filter "FullyQualifiedName~DemoSeedTests"`

Beklenen: `Başarılı! - Başarısız: 0, Başarılı: 4`

- [ ] **Adım 6: Tüm süiti koş**

Çalıştır: `./scripts/test-hizli.sh && dotnet test tests/Poyra.Tests.Integration`

Beklenen: allnde `Başarısız: 0`.

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
