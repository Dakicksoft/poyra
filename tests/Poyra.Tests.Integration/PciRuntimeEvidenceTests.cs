using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Poyra.Api;
using Poyra.Modules.Vault;
using Poyra.SharedKernel.Tenancy;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// PCI KANIT PAKETİ — çalışma zamanı kanıtları.
///
/// Yapısal testler "böyle bir sütun yok" der; buradakiler GERÇEK VERİTABANINA bakar:
/// kart kaydedildikten sonra diskte PAN'ın hiçbir kopyası kalmıyor mu, zarf gerçekten
/// kurcalanamaz mı, token kaldırılınca kart geri getirilebiliyor mu.
///
/// Denetçiye gösterilecek olan budur: iddia değil, koşan kanıt.
/// </summary>
[Collection("postgres")]
public sealed class PciRuntimeEvidenceTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string TestPan = "4155650100416111";
    private const string TestCvv = "8341";

    /// <summary>
    /// Kart sahibi adı taramada KULLANILIR çünkü uzun ve ayırt edicidir.
    /// CVV içerik taramasına GİRMEZ: 3-4 haneli bir sayı tutarlarda, gecikme
    /// ölçümlerinde ve UUID parçalarında rastlantısal eşleşir; testi gürültüye boğar.
    /// CVV'nin saklanmadığı iki KESİN yolla kanıtlanır: (1) yapısal testte sütunun
    /// kendisi yoktur, (2) aşağıda şifreli zarf ÇÖZÜLÜP içinde CVV olmadığı gösterilir.
    /// </summary>
    private const string TestHolder = "AYSE PCIKANIT YILMAZ";

    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _api;

    public PciRuntimeEvidenceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _factory = new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Platform:AdminKey", AdminKey);
            builder.UseSetting("Poyra:PublicBaseUrl", "http://localhost");
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Poyra:JwtKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            builder.UseSetting("Poyra:VaultKey", Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
        });
        _api = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string ApiKey);
    private sealed record AccountDto(Guid Id);
    private sealed record CardDto(string Token, string MaskedPan, string Brand, int ExpiryMonth, int ExpiryYear);
    private sealed record PaymentDto(string Id, string Status);

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        return await _api.SendAsync(request);
    }

    private async Task<T> SendOk<T>(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var response = await Send(method, path, body, headers);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<TenantCreated> SeedTenantAsync()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "PCI Kanıt A.Ş.", slug = "pci-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));

        await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        return tenant;
    }

    private Task<CardDto> StoreCardAsync(string apiKey) => SendOk<CardDto>(
        HttpMethod.Post, "/v1/vault/cards", new
        {
            cardNumber = TestPan,
            expiryMonth = 12,
            expiryYear = 2030,
            cvv = TestCvv,
            holderName = TestHolder,
            customerRef = "musteri-1",
        }, ("X-Api-Key", apiKey));

    /// <summary>Bütün veritabanını metin olarak tarar — PAN'ın HERHANGİ bir kopyasını arar.</summary>
    private async Task<List<string>> ScanDatabaseForAsync(params string[] needles)
    {
        var hits = new List<string>();
        await using var connection = new NpgsqlConnection(_fixture.OwnerCs);
        await connection.OpenAsync();

        var tables = new List<string>();
        await using (var listCommand = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables "
            + "WHERE table_schema = 'public' AND table_type = 'BASE TABLE'", connection))
        await using (var reader = await listCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
        }

        foreach (var table in tables)
        {
            // Satırın TAMAMINI metne çevir (bytea dahil) ve içinde ara — sütun adına
            // bakmak yetmez, PAN bir JSON alanına ya da hata mesajına da sızabilir.
            foreach (var needle in needles)
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT count(*) FROM \"{table}\" t WHERE t::text LIKE @needle", connection);
                command.Parameters.AddWithValue("needle", $"%{needle}%");

                if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0)
                    hits.Add($"{table} ← '{needle}'");
            }
        }

        return hits;
    }

    // ---- Kanıtlar --------------------------------------------------------------

    [Fact]
    public async Task Kart_saklandiktan_sonra_veritabaninda_PAN_kopyasi_kalmamali()
    {
        var tenant = await SeedTenantAsync();
        var card = await StoreCardAsync(tenant.ApiKey);

        card.MaskedPan.ShouldBe("415565******6111");
        card.Token.ShouldStartWith("tok_");

        // TÜM tablolarda düz PAN ve CVV aranır — sütun adına değil İÇERİĞE bakılır
        var hits = await ScanDatabaseForAsync(TestPan, TestHolder);

        hits.ShouldBeEmpty(
            "PCI DSS 3.4: kart saklandıktan sonra veritabanının HİÇBİR yerinde düz PAN, CVV "
            + "veya kart sahibi adı bulunmamalı. Bulunan: " + string.Join(" · ", hits));
    }

    [Fact]
    public async Task Odeme_akisi_PAN_i_diske_yazmamali()
    {
        var tenant = await SeedTenantAsync();

        var created = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 25_000, currency = "TRY" }, ("X-Api-Key", tenant.ApiKey));

        await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{created.Id}/confirm-direct", new
        {
            cardNumber = TestPan,
            expiryMonth = 12,
            expiryYear = 2030,
            cvv = TestCvv,
            holderName = TestHolder,
        }, ("X-Api-Key", tenant.ApiKey));

        // Direct akış kartı BANKAYA gönderir ama hiçbir yere YAZMAZ:
        // olay defteri, deneme kaydı, outbox ve webhook gövdeleri dahil
        var hits = await ScanDatabaseForAsync(TestPan, TestHolder);
        hits.ShouldBeEmpty("Ödeme akışı kart verisini diske yazamaz. Bulunan: "
                           + string.Join(" · ", hits));
    }

    [Fact]
    public async Task Sifreli_zarfin_icinde_CVV_olmamali()
    {
        var tenant = await SeedTenantAsync();
        var card = await StoreCardAsync(tenant.ApiKey);

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<VaultDbContext>();
        var crypto = scope.ServiceProvider
            .GetRequiredService<Poyra.Modules.Vault.Infrastructure.VaultCrypto>();

        var record = await db.CardTokens.AsNoTracking().SingleAsync(t => t.PublicToken == card.Token);

        // Zarfı ÇÖZ ve içine bak: PAN var (olmalı), CVV YOK (olmamalı).
        // İçerik taraması 4 haneli CVV için güvenilmez; bu kanıt kesindir.
        var decrypted = crypto.Unprotect(record.CardEncrypted);
        decrypted.Pan.ShouldBe(TestPan);
        decrypted.Cvv.ShouldBeNull("PCI DSS 3.2: doğrulama değeri zarfa dahi alınamaz.");
        decrypted.HolderName.ShouldBe(TestHolder); // ad yalnız BURADA yaşar

        // Zarfın ham baytlarında CVV'nin metin karşılığı da geçmemeli
        Convert.ToHexString(record.CardEncrypted)
            .ShouldNotContain(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(TestCvv)));
    }

    [Fact]
    public async Task Zarf_kurcalanirsa_cozulmemeli()
    {
        var tenant = await SeedTenantAsync();
        var card = await StoreCardAsync(tenant.ApiKey);

        // Zarfın tek baytını değiştir: AES-GCM etiketi tutmaz, çözme PATLAR.
        // Sessizce bozuk veri dönmesi, kurcalanmış kartla işlem yapmak demektir.
        await using (var connection = new NpgsqlConnection(_fixture.OwnerCs))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE card_tokens SET card_encrypted = "
                + "set_byte(card_encrypted, length(card_encrypted) - 1, "
                + "  (get_byte(card_encrypted, length(card_encrypted) - 1) # 255)) "
                + "WHERE public_token = @token", connection);
            command.Parameters.AddWithValue("token", card.Token);
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        using var scope = _factory.Services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantContext.Set(tenant.TenantId);
        var vault = scope.ServiceProvider
            .GetRequiredService<Poyra.Modules.Vault.Contracts.ICardVault>();

        await Should.ThrowAsync<CryptographicException>(
            async () => await vault.ResolveAsync(card.Token, default));
    }

    [Fact]
    public async Task Ayni_kart_iki_kez_saklanirsa_zarflar_farkli_olmali()
    {
        var tenant = await SeedTenantAsync();
        await StoreCardAsync(tenant.ApiKey);

        // Aynı kart farklı müşteriye
        await SendOk<CardDto>(HttpMethod.Post, "/v1/vault/cards", new
        {
            cardNumber = TestPan,
            expiryMonth = 12,
            expiryYear = 2030,
            cvv = TestCvv,
            holderName = TestHolder,
            customerRef = "musteri-2",
        }, ("X-Api-Key", tenant.ApiKey));

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<VaultDbContext>();

        var records = await db.CardTokens.AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .Select(t => new { t.CardEncrypted, t.Fingerprint })
            .ToListAsync();

        records.Count.ShouldBe(2);

        // Nonce her şifrelemede yeniden üretilir → aynı düz metin farklı şifreli metin.
        // Aynı çıkması, nonce'un tekrar kullanıldığını ve GCM'in kırıldığını gösterirdi.
        Convert.ToHexString(records[0].CardEncrypted)
            .ShouldNotBe(Convert.ToHexString(records[1].CardEncrypted));

        // Parmak izi ise AYNI: tekilleştirme çalışır, PAN türetilemez (HMAC)
        records[0].Fingerprint.ShouldBe(records[1].Fingerprint);
        records[0].Fingerprint.ShouldNotContain(TestPan);
    }

    [Fact]
    public async Task Token_kaldirilinca_kart_kriptografik_olarak_imha_edilmeli()
    {
        var tenant = await SeedTenantAsync();
        var card = await StoreCardAsync(tenant.ApiKey);

        (await Send(HttpMethod.Delete, $"/v1/vault/cards/{card.Token}", null,
            ("X-Api-Key", tenant.ApiKey))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<VaultDbContext>();

        var record = await db.CardTokens.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(t => t.PublicToken == card.Token);

        // Kayıt SİLİNMEZ (İlke 3: iz kalır) ama zarf BOŞALIR — kart geri getirilemez
        record.DeletedAt.ShouldNotBeNull();
        record.CardEncrypted.ShouldBeEmpty();
        record.MaskedPan.ShouldBe("415565******6111"); // maske denetim için kalır

        (await ScanDatabaseForAsync(TestPan)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Kasa_uclari_kart_verisini_geri_dondurmemeli()
    {
        var tenant = await SeedTenantAsync();
        var card = await StoreCardAsync(tenant.ApiKey);

        var listBody = await (await Send(HttpMethod.Get, "/v1/vault/cards", null,
            ("X-Api-Key", tenant.ApiKey))).Content.ReadAsStringAsync();

        // Liste yanıtı yalnız maske ve token taşır — kart geri okunamaz
        listBody.ShouldContain(card.Token);
        listBody.ShouldContain("415565******6111");
        listBody.ShouldNotContain(TestPan);
        listBody.ShouldNotContain(TestCvv);
        listBody.ShouldNotContain("cardEncrypted");
        listBody.ShouldNotContain("fingerprint");
    }

    [Fact]
    public async Task Hatali_kart_denemesi_hata_govdesinde_PAN_sizdirmamali()
    {
        var tenant = await SeedTenantAsync();

        // Luhn'a takılan kart: doğrulama hatası dönerken PAN'ı yankılamamalı
        var response = await Send(HttpMethod.Post, "/v1/vault/cards", new
        {
            cardNumber = "4155650100416112", // son hane bozuk
            expiryMonth = 12,
            expiryYear = 2030,
            cvv = TestCvv,
            customerRef = "musteri-x",
        }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();

        // Doğrulama hataları girdiyi yankılamaya meyillidir — burada yasak
        body.ShouldNotContain("4155650100416112");
        body.ShouldNotContain(TestCvv);
    }
}
