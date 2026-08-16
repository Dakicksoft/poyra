using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F7 pekiştirme: ekip ve API anahtarı yönetimi. Bu iki yüzey, işyerinin kendi
/// güvenliğini yönetebildiği tek yerdir — sızan anahtar döndürülemiyorsa ya da
/// ayrılan çalışanın erişimi kesilemiyorsa ürün eksiktir.
/// </summary>
[Collection("postgres")]
public sealed class TeamAndKeyManagementTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "ekip-parola-1234";

    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _api;

    public TeamAndKeyManagementTests(PostgresFixture fixture)
    {
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

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record TenantUser(
        Guid UserId, string Email, string DisplayName, string Role,
        bool IsActive, bool EmailVerified, bool IsSelf, DateTimeOffset JoinedAt);
    private sealed record ApiKeyRow(
        Guid Id, string Name, string PrefixHint, bool Revoked,
        DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
    private sealed record CreatedKey(Guid Id, string Name, string PrefixHint, string Key);
    private sealed record TenantSummary(Guid Id, string Slug, string Name);
    private sealed record AuthTokens(
        string AccessToken, int ExpiresInSeconds, string RefreshToken,
        TenantSummary Tenant, string Role, Guid UserId);
    private sealed record CreatedUser(Guid UserId, string Email, string DisplayName, string Role);

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

    private async Task<(TenantCreated Tenant, string OwnerEmail)> SeedAsync()
    {
        var email = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Ekip A.Ş.",
            slug = "ekp-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "Sahip",
        }, ("X-Platform-Key", AdminKey));

        return (tenant, email);
    }

    private async Task<(Guid UserId, string Email)> AddUserAsync(string apiKey, string role)
    {
        var email = $"uye-{Guid.NewGuid():N}@ornek.com";
        var created = await SendOk<CreatedUser>(HttpMethod.Post, "/v1/users",
            new { email, password = Password, displayName = "Üye", role }, ("X-Api-Key", apiKey));
        return (created.UserId, email);
    }

    // ---- Kullanıcı listesi ------------------------------------------------------

    [Fact]
    public async Task Kullanici_listesi_rolleri_ve_uyeligi_gostermeli()
    {
        var (tenant, ownerEmail) = await SeedAsync();
        var (_, memberEmail) = await AddUserAsync(tenant.ApiKey, "operations");

        var users = await SendOk<List<TenantUser>>(
            HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey));

        users.Count.ShouldBe(2);
        users.Single(u => u.Email == ownerEmail).Role.ShouldBe("owner");
        users.Single(u => u.Email == memberEmail).Role.ShouldBe("operations");

        // Makine çağrısında (X-Api-Key) "ben" diye biri yoktur — hiçbir satır IsSelf olmamalı,
        // yoksa panel yanlış satırın rol kutusunu kilitlerdi.
        users.ShouldAllBe(u => !u.IsSelf);
    }

    [Fact]
    public async Task Kullanici_listesi_baska_isyerini_sizdirmamali()
    {
        var (first, firstOwner) = await SeedAsync();
        var (second, _) = await SeedAsync();

        var users = await SendOk<List<TenantUser>>(
            HttpMethod.Get, "/v1/users", null, ("X-Api-Key", second.ApiKey));

        // users/user_tenants RLS'siz platform tablolarıdır: filtre EL İLE konur ve
        // bir kez unutulursa tüm platformun kullanıcı listesi sızar.
        users.ShouldNotContain(u => u.Email == firstOwner);
        users.Count.ShouldBe(1);
    }

    // ---- Rol değişimi -----------------------------------------------------------

    [Fact]
    public async Task Rol_degistirilebilmeli()
    {
        var (tenant, _) = await SeedAsync();
        var (userId, email) = await AddUserAsync(tenant.ApiKey, "auditor");

        var updated = await SendOk<TenantUser>(HttpMethod.Post, $"/v1/users/{userId}/role",
            new { role = "finance" }, ("X-Api-Key", tenant.ApiKey));

        updated.Email.ShouldBe(email);
        updated.Role.ShouldBe("finance");

        var users = await SendOk<List<TenantUser>>(
            HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey));
        users.Single(u => u.UserId == userId).Role.ShouldBe("finance");
    }

    [Fact]
    public async Task Gecersiz_rol_reddedilmeli()
    {
        var (tenant, _) = await SeedAsync();
        var (userId, _) = await AddUserAsync(tenant.ApiKey, "auditor");

        var response = await Send(HttpMethod.Post, $"/v1/users/{userId}/role",
            new { role = "superadmin" }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Son_sahibin_rolu_dusurulememeli()
    {
        var (tenant, ownerEmail) = await SeedAsync();
        var users = await SendOk<List<TenantUser>>(
            HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey));
        var ownerId = users.Single(u => u.Email == ownerEmail).UserId;

        // Kilitlenme senaryosu: tek sahip kendini auditor yaparsa işyerinde POS
        // ekleyebilecek kimse kalmaz ve rolü geri verebilecek kimse de yoktur.
        var response = await Send(HttpMethod.Post, $"/v1/users/{ownerId}/role",
            new { role = "auditor" }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("user.last_owner");
    }

    [Fact]
    public async Task Ikinci_sahip_atandiktan_sonra_ilk_sahip_dusurulebilmeli()
    {
        var (tenant, ownerEmail) = await SeedAsync();
        await AddUserAsync(tenant.ApiKey, "owner");

        var users = await SendOk<List<TenantUser>>(
            HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey));
        var firstOwnerId = users.Single(u => u.Email == ownerEmail).UserId;

        var updated = await SendOk<TenantUser>(HttpMethod.Post, $"/v1/users/{firstOwnerId}/role",
            new { role = "admin" }, ("X-Api-Key", tenant.ApiKey));

        updated.Role.ShouldBe("admin"); // koruma "hiç düşürülemez" değil, "sahipsiz kalmaz" demek
    }

    // ---- Erişimi kaldırma -------------------------------------------------------

    [Fact]
    public async Task Erisim_kaldirilinca_acik_oturum_ANINDA_kapanmali()
    {
        var (tenant, _) = await SeedAsync();
        var (userId, email) = await AddUserAsync(tenant.ApiKey, "operations");

        // Kullanıcı giriş yapmış ve elinde 30 gün ömürlü bir refresh token var
        var tokens = await SendOk<AuthTokens>(HttpMethod.Post, "/v1/auth/login",
            new { email, password = Password, tenantSlug = tenant.Slug });
        tokens.RefreshToken.ShouldNotBeNullOrWhiteSpace();

        // Token gerçekten çalışıyor (aksi halde test bir şey kanıtlamaz)
        var beforeRefresh = await Send(HttpMethod.Post, "/v1/auth/refresh",
            new { refreshToken = tokens.RefreshToken });
        beforeRefresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = (await beforeRefresh.Content.ReadFromJsonAsync<AuthTokens>())!;

        await SendOk<object>(HttpMethod.Delete, $"/v1/users/{userId}", null, ("X-Api-Key", tenant.ApiKey));

        // ★ Üyelik silmek yetmez: belirteç iptal edilmezse kişi 30 gün daha erişirdi
        var afterRefresh = await Send(HttpMethod.Post, "/v1/auth/refresh",
            new { refreshToken = rotated.RefreshToken });
        afterRefresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var users = await SendOk<List<TenantUser>>(
            HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey));
        users.ShouldNotContain(u => u.UserId == userId);
    }

    [Fact]
    public async Task Erisim_kaldirmak_kullanici_hesabini_SILMEMELI()
    {
        var (first, _) = await SeedAsync();
        var (second, _) = await SeedAsync();

        // Aynı kişi iki işyerinde çalışıyor
        var email = $"ortak-{Guid.NewGuid():N}@ornek.com";
        var created = await SendOk<CreatedUser>(HttpMethod.Post, "/v1/users",
            new { email, password = Password, displayName = "Ortak", role = "operations" },
            ("X-Api-Key", first.ApiKey));
        await SendOk<CreatedUser>(HttpMethod.Post, "/v1/users",
            new { email, password = Password, displayName = "Ortak", role = "finance" },
            ("X-Api-Key", second.ApiKey));

        await SendOk<object>(HttpMethod.Delete, $"/v1/users/{created.UserId}", null,
            ("X-Api-Key", first.ApiKey));

        // İkinci işyerindeki işi devam ediyor — hesabı silmek onu da işsiz bırakırdı
        var login = await Send(HttpMethod.Post, "/v1/auth/login",
            new { email, password = Password, tenantSlug = second.Slug });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stillThere = await SendOk<List<TenantUser>>(
            HttpMethod.Get, "/v1/users", null, ("X-Api-Key", second.ApiKey));
        stillThere.ShouldContain(u => u.UserId == created.UserId);
    }

    [Fact]
    public async Task Son_sahip_kaldirilamamali()
    {
        var (tenant, ownerEmail) = await SeedAsync();
        var users = await SendOk<List<TenantUser>>(
            HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey));
        var ownerId = users.Single(u => u.Email == ownerEmail).UserId;

        var response = await Send(HttpMethod.Delete, $"/v1/users/{ownerId}", null,
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("user.last_owner");
    }

    [Fact]
    public async Task Baska_isyerinin_kullanicisi_kaldirilamamali()
    {
        var (first, _) = await SeedAsync();
        var (second, _) = await SeedAsync();
        var (victimId, _) = await AddUserAsync(first.ApiKey, "operations");

        var response = await Send(HttpMethod.Delete, $"/v1/users/{victimId}", null,
            ("X-Api-Key", second.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Kurban hâlâ kendi işyerinde
        var users = await SendOk<List<TenantUser>>(
            HttpMethod.Get, "/v1/users", null, ("X-Api-Key", first.ApiKey));
        users.ShouldContain(u => u.UserId == victimId);
    }

    // ---- API anahtarları --------------------------------------------------------

    [Fact]
    public async Task Anahtar_listesi_duz_degeri_ASLA_dondurmemeli()
    {
        var (tenant, _) = await SeedAsync();

        var response = await Send(HttpMethod.Get, "/v1/api-keys", null, ("X-Api-Key", tenant.ApiKey));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // ★ İşyeri oluşturmada bir kez gösterilen anahtar, listede bir daha görünmemeli
        body.ShouldNotContain(tenant.ApiKey);

        var keys = System.Text.Json.JsonSerializer.Deserialize<List<ApiKeyRow>>(body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        keys.Count.ShouldBe(1);
        keys[0].Revoked.ShouldBeFalse();
        tenant.ApiKey.ShouldStartWith(keys[0].PrefixHint); // önek hangi anahtar olduğunu ayırt eder
    }

    [Fact]
    public async Task Uretilen_anahtar_hemen_calismali_ve_isyerine_baglanmali()
    {
        var (tenant, _) = await SeedAsync();

        var created = await SendOk<CreatedKey>(HttpMethod.Post, "/v1/api-keys",
            new { name = "Üretim sunucusu" }, ("X-Api-Key", tenant.ApiKey));

        created.Key.ShouldStartWith("sk_test_"); // live=false varsayılanı
        created.PrefixHint.ShouldNotBeNullOrWhiteSpace();

        // Yeni anahtarla yapılan çağrı AYNI işyerini görmeli
        var users = await SendOk<List<TenantUser>>(
            HttpMethod.Get, "/v1/users", null, ("X-Api-Key", created.Key));
        users.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Canli_anahtar_oneki_ayirt_edilebilmeli()
    {
        var (tenant, _) = await SeedAsync();

        var created = await SendOk<CreatedKey>(HttpMethod.Post, "/v1/api-keys",
            new { name = "Canlı", live = true }, ("X-Api-Key", tenant.ApiKey));

        // Test ve canlı anahtarın karışması, test kartıyla gerçek para çekmeye
        // (ya da tersine) yol açar — önek gözle ayırt edilebilir olmalı
        created.Key.ShouldStartWith("sk_live_");
    }

    [Fact]
    public async Task Iptal_edilen_anahtar_401_almali_ve_kayit_SILINMEMELI()
    {
        var (tenant, _) = await SeedAsync();
        var created = await SendOk<CreatedKey>(HttpMethod.Post, "/v1/api-keys",
            new { name = "Sızan anahtar" }, ("X-Api-Key", tenant.ApiKey));

        // Doğru sıra: yeni anahtar üret → dağıt → eskisini kapat
        await SendOk<ApiKeyRow>(HttpMethod.Post, $"/v1/api-keys/{created.Id}/revoke", null,
            ("X-Api-Key", tenant.ApiKey));

        var blocked = await Send(HttpMethod.Get, "/v1/users", null, ("X-Api-Key", created.Key));
        blocked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Kayıt duruyor (İlke 3): sızıntı incelemesi "ne zaman kapatıldı" diye sorar
        var keys = await SendOk<List<ApiKeyRow>>(
            HttpMethod.Get, "/v1/api-keys", null, ("X-Api-Key", tenant.ApiKey));
        var revoked = keys.Single(k => k.Id == created.Id);
        revoked.Revoked.ShouldBeTrue();
        revoked.RevokedAt.ShouldNotBeNull();
        revoked.Name.ShouldBe("Sızan anahtar");
    }

    [Fact]
    public async Task Son_aktif_anahtar_iptal_edilememeli()
    {
        var (tenant, _) = await SeedAsync();
        var keys = await SendOk<List<ApiKeyRow>>(
            HttpMethod.Get, "/v1/api-keys", null, ("X-Api-Key", tenant.ApiKey));

        // Tek anahtarını kapatan işyeri tahsilatını ANINDA durdurur ve geri açacak
        // anahtarı da kalmaz — kilitlenme.
        var response = await Send(HttpMethod.Post, $"/v1/api-keys/{keys[0].Id}/revoke", null,
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("api_key.last_active");

        // Anahtar hâlâ çalışıyor
        (await Send(HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Anahtar_dondurme_akisi_kesintisiz_olmali()
    {
        var (tenant, _) = await SeedAsync();

        // ① Yeni anahtar üret
        var fresh = await SendOk<CreatedKey>(HttpMethod.Post, "/v1/api-keys",
            new { name = "Yeni" }, ("X-Api-Key", tenant.ApiKey));

        // ② İki anahtar da AYNI ANDA çalışır — dağıtım penceresi budur
        (await Send(HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Send(HttpMethod.Get, "/v1/users", null, ("X-Api-Key", fresh.Key)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // ③ Eskisini kapat — artık son aktif değil, izin verilir
        var keys = await SendOk<List<ApiKeyRow>>(
            HttpMethod.Get, "/v1/api-keys", null, ("X-Api-Key", tenant.ApiKey));
        var old = keys.Single(k => k.Id != fresh.Id);
        await SendOk<ApiKeyRow>(HttpMethod.Post, $"/v1/api-keys/{old.Id}/revoke", null,
            ("X-Api-Key", fresh.Key));

        (await Send(HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Get, "/v1/users", null, ("X-Api-Key", fresh.Key)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Anahtar_sayisi_sinirli_olmali()
    {
        var (tenant, _) = await SeedAsync();

        // İşyeri 1 anahtarla doğar; sınır 10
        for (var i = 2; i <= 10; i++)
            await SendOk<CreatedKey>(HttpMethod.Post, "/v1/api-keys",
                new { name = $"Anahtar {i}" }, ("X-Api-Key", tenant.ApiKey));

        var overflow = await Send(HttpMethod.Post, "/v1/api-keys",
            new { name = "Onbirinci" }, ("X-Api-Key", tenant.ApiKey));

        overflow.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await overflow.Content.ReadAsStringAsync()).ShouldContain("api_key.too_many");
    }

    [Fact]
    public async Task Baska_isyerinin_anahtari_iptal_edilememeli()
    {
        var (first, _) = await SeedAsync();
        var (second, _) = await SeedAsync();

        // Kurbanın iki anahtarı olsun ki reddin sebebi "son aktif" olmasın
        await SendOk<CreatedKey>(HttpMethod.Post, "/v1/api-keys",
            new { name = "İkinci" }, ("X-Api-Key", first.ApiKey));
        var victimKeys = await SendOk<List<ApiKeyRow>>(
            HttpMethod.Get, "/v1/api-keys", null, ("X-Api-Key", first.ApiKey));

        var response = await Send(HttpMethod.Post, $"/v1/api-keys/{victimKeys[0].Id}/revoke", null,
            ("X-Api-Key", second.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Kurbanın anahtarları duruyor
        var after = await SendOk<List<ApiKeyRow>>(
            HttpMethod.Get, "/v1/api-keys", null, ("X-Api-Key", first.ApiKey));
        after.ShouldAllBe(k => !k.Revoked);
    }

    [Fact]
    public async Task Adsiz_anahtar_reddedilmeli()
    {
        var (tenant, _) = await SeedAsync();

        var response = await Send(HttpMethod.Post, "/v1/api-keys",
            new { name = "" }, ("X-Api-Key", tenant.ApiKey));

        // Adsız anahtar, sızıntı anında "hangisini kapatacağım" sorusunu cevapsız bırakır
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---- Rol kapısı -------------------------------------------------------------

    [Fact]
    public async Task Anahtar_yonetimi_yalniz_sahibe_acik_olmali()
    {
        var (tenant, _) = await SeedAsync();
        var (_, adminEmail) = await AddUserAsync(tenant.ApiKey, "admin");

        var tokens = await SendOk<AuthTokens>(HttpMethod.Post, "/v1/auth/login",
            new { email = adminEmail, password = Password, tenantSlug = tenant.Slug });

        // admin kullanıcı yönetebilir…
        var users = await Send(HttpMethod.Get, "/v1/users", null,
            ("Authorization", $"Bearer {tokens.AccessToken}"));
        users.StatusCode.ShouldBe(HttpStatusCode.OK);

        // …ama anahtar üretemez: anahtar işyerinin TÜM verisine sınırsız erişimdir
        var create = await Send(HttpMethod.Post, "/v1/api-keys", new { name = "Yetkisiz" },
            ("Authorization", $"Bearer {tokens.AccessToken}"));
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await create.Content.ReadAsStringAsync()).ShouldContain("owner");
    }

    [Fact]
    public async Task Denetci_kullanici_yonetememeli()
    {
        var (tenant, _) = await SeedAsync();
        var (_, auditorEmail) = await AddUserAsync(tenant.ApiKey, "auditor");
        var (victimId, _) = await AddUserAsync(tenant.ApiKey, "operations");

        var tokens = await SendOk<AuthTokens>(HttpMethod.Post, "/v1/auth/login",
            new { email = auditorEmail, password = Password, tenantSlug = tenant.Slug });

        var remove = await Send(HttpMethod.Delete, $"/v1/users/{victimId}", null,
            ("Authorization", $"Bearer {tokens.AccessToken}"));
        remove.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Okuma serbest: kimin erişimi olduğunu görmek denetçinin işidir
        var list = await Send(HttpMethod.Get, "/v1/users", null,
            ("Authorization", $"Bearer {tokens.AccessToken}"));
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Kullanici_kendi_rolunu_dusurememeli()
    {
        var (tenant, _) = await SeedAsync();
        await AddUserAsync(tenant.ApiKey, "owner"); // kilit ikinci sahip olsa da tutmalı
        var (adminId, adminEmail) = await AddUserAsync(tenant.ApiKey, "admin");

        var tokens = await SendOk<AuthTokens>(HttpMethod.Post, "/v1/auth/login",
            new { email = adminEmail, password = Password, tenantSlug = tenant.Slug });

        var response = await Send(HttpMethod.Post, $"/v1/users/{adminId}/role",
            new { role = "auditor" }, ("Authorization", $"Bearer {tokens.AccessToken}"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("user.cannot_demote_self");
    }

    [Fact]
    public async Task Kullanici_kendini_kaldiramamali()
    {
        var (tenant, _) = await SeedAsync();
        var (adminId, adminEmail) = await AddUserAsync(tenant.ApiKey, "admin");

        var tokens = await SendOk<AuthTokens>(HttpMethod.Post, "/v1/auth/login",
            new { email = adminEmail, password = Password, tenantSlug = tenant.Slug });

        var response = await Send(HttpMethod.Delete, $"/v1/users/{adminId}", null,
            ("Authorization", $"Bearer {tokens.AccessToken}"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("user.cannot_remove_self");
    }

    [Fact]
    public async Task Yetersiz_rol_kendini_yukseltememeli()
    {
        var (tenant, _) = await SeedAsync();
        var (opsId, opsEmail) = await AddUserAsync(tenant.ApiKey, "operations");

        var tokens = await SendOk<AuthTokens>(HttpMethod.Post, "/v1/auth/login",
            new { email = opsEmail, password = Password, tenantSlug = tenant.Slug });

        // operations kullanıcı kendini owner yapamaz — çünkü /v1/users yazma kapısı
        // zaten 'admin' ister. Yükseltme yasağı ayrı bir kurala gerek bırakmaz.
        var response = await Send(HttpMethod.Post, $"/v1/users/{opsId}/role",
            new { role = "owner" }, ("Authorization", $"Bearer {tokens.AccessToken}"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
