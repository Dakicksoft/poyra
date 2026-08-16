using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F1.3 kimlik akışı: işyeri+sahip → login → JWT ile /me → refresh rotation →
/// eski belirteç ölür → logout. Rol koruması: auditor yazamaz, operations ödeme açar
/// ama bağlantı hesabı ekleyemez; makine (API anahtarı) rolden muaftır.
/// </summary>
[Collection("postgres")]
public sealed class AuthFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _client;

    public AuthFlowTests(PostgresFixture fixture)
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
        });
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey, Guid? OwnerUserId);
    private sealed record TenantInfo(Guid Id, string Slug, string Name);
    private sealed record Tokens(string AccessToken, int ExpiresInSeconds, string RefreshToken, TenantInfo Tenant, string Role);
    private sealed record Me(Guid UserId, string Email, string DisplayName, Guid TenantId, string Role);
    private sealed record UserCreated(Guid UserId, string Email, string Role);
    private sealed record AccountCreated(Guid Id);

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        return _client.SendAsync(request);
    }

    private async Task<T> SendOk<T>(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var response = await Send(method, path, body, headers);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<TenantCreated> CreateTenantWithOwnerAsync(string email, string password)
        => await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Kimlik Testi",
            slug = "auth-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = password,
            ownerName = "Sahip Kullanıcı",
        }, ("X-Platform-Key", AdminKey));

    [Fact]
    public async Task Login_me_refresh_rotation_ve_logout_akisi()
    {
        var email = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await CreateTenantWithOwnerAsync(email, "cok-gizli-parola-1");
        tenant.OwnerUserId.ShouldNotBeNull();

        // Yanlış parola → 401 (hesap keşfine kapalı tek mesaj)
        var bad = await Send(HttpMethod.Post, "/v1/auth/login",
            new { email, password = "yanlis" });
        bad.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Giriş
        var tokens = await SendOk<Tokens>(HttpMethod.Post, "/v1/auth/login",
            new { email, password = "cok-gizli-parola-1" });
        tokens.Role.ShouldBe("owner");
        tokens.RefreshToken.ShouldStartWith("prt_");
        tokens.Tenant.Id.ShouldBe(tenant.TenantId);

        // JWT ile kimim?
        var me = await SendOk<Me>(HttpMethod.Get, "/v1/auth/me", null,
            ("Authorization", $"Bearer {tokens.AccessToken}"));
        me.Email.ShouldBe(email);
        me.Role.ShouldBe("owner");
        me.TenantId.ShouldBe(tenant.TenantId);

        // Refresh rotation: yeni çift gelir, eski refresh ölür
        var rotated = await SendOk<Tokens>(HttpMethod.Post, "/v1/auth/refresh",
            new { refreshToken = tokens.RefreshToken });
        rotated.RefreshToken.ShouldNotBe(tokens.RefreshToken);

        var reuse = await Send(HttpMethod.Post, "/v1/auth/refresh",
            new { refreshToken = tokens.RefreshToken });
        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Logout → yenisi de ölür
        await SendOk<object>(HttpMethod.Post, "/v1/auth/logout",
            new { refreshToken = rotated.RefreshToken });
        var afterLogout = await Send(HttpMethod.Post, "/v1/auth/refresh",
            new { refreshToken = rotated.RefreshToken });
        afterLogout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Geçersiz JWT ile korunan uca girilemez
        var invalid = await Send(HttpMethod.Get, "/v1/auth/me", null,
            ("Authorization", "Bearer sahte-belirteç"));
        invalid.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rol_korumasi_yazma_uclarinda_uygulanmali()
    {
        var ownerEmail = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await CreateTenantWithOwnerAsync(ownerEmail, "cok-gizli-parola-1");

        // Sahip, denetçi ve operasyon kullanıcıları açar (Admin+ gerektirir — makine anahtarıyla)
        var auditorEmail = $"denetci-{Guid.NewGuid():N}@ornek.com";
        var opsEmail = $"ops-{Guid.NewGuid():N}@ornek.com";
        await SendOk<UserCreated>(HttpMethod.Post, "/v1/users",
            new { email = auditorEmail, password = "denetci-parola-1", displayName = "Denetçi", role = "auditor" },
            ("X-Api-Key", tenant.ApiKey));
        await SendOk<UserCreated>(HttpMethod.Post, "/v1/users",
            new { email = opsEmail, password = "operasyon-parola-1", displayName = "Operasyon", role = "operations" },
            ("X-Api-Key", tenant.ApiKey));

        var auditor = await SendOk<Tokens>(HttpMethod.Post, "/v1/auth/login",
            new { email = auditorEmail, password = "denetci-parola-1" });
        var ops = await SendOk<Tokens>(HttpMethod.Post, "/v1/auth/login",
            new { email = opsEmail, password = "operasyon-parola-1" });

        // Denetçi: okuyabilir, yazamaz
        (await Send(HttpMethod.Get, "/v1/connector-accounts", null,
            ("Authorization", $"Bearer {auditor.AccessToken}"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var auditorWrite = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 1_000 }, ("Authorization", $"Bearer {auditor.AccessToken}"));
        auditorWrite.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await auditorWrite.Content.ReadAsStringAsync()).ShouldContain("insufficient_role");

        // Operasyon: ödeme açabilir, bağlantı hesabı EKLEYEMEZ (Admin ister)
        (await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 1_000 },
            ("Authorization", $"Bearer {ops.AccessToken}"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Send(HttpMethod.Post, "/v1/connector-accounts",
            new { connectorKey = "mockbank", label = "X", credentials = new Dictionary<string, string> { ["secret"] = "s" } },
            ("Authorization", $"Bearer {ops.AccessToken}"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Kullanıcı açma da Admin+ ister: operasyon deneyince 403
        (await Send(HttpMethod.Post, "/v1/users",
            new { email = "x@y.z", password = "0123456789", displayName = "X", role = "auditor" },
            ("Authorization", $"Bearer {ops.AccessToken}"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Makine (API anahtarı) rolden muaf: bağlantı hesabı ekleyebilir
        (await Send(HttpMethod.Post, "/v1/connector-accounts",
            new
            {
                connectorKey = "mockbank",
                label = "Makine POS",
                credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            },
            ("X-Api-Key", tenant.ApiKey))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Defter_yazma_uclari_Finance_esigine_tabi_olmali()
    {
        // Gözden geçirmede bulunan boşluk: /v1/settlements ve /v1/ledger rol tablosunda
        // yoktu — salt-okur denetçi bile ekstre yükleyip finansman oranı değiştirebiliyordu.
        var ownerEmail = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await CreateTenantWithOwnerAsync(ownerEmail, "cok-gizli-parola-1");

        var account = await SendOk<AccountCreated>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Defter POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        var auditorEmail = $"denetci-{Guid.NewGuid():N}@ornek.com";
        var financeEmail = $"finans-{Guid.NewGuid():N}@ornek.com";
        await SendOk<UserCreated>(HttpMethod.Post, "/v1/users",
            new { email = auditorEmail, password = "denetci-parola-1", displayName = "Denetçi", role = "auditor" },
            ("X-Api-Key", tenant.ApiKey));
        await SendOk<UserCreated>(HttpMethod.Post, "/v1/users",
            new { email = financeEmail, password = "finans-parola-11", displayName = "Finans", role = "finance" },
            ("X-Api-Key", tenant.ApiKey));

        var auditor = await SendOk<Tokens>(HttpMethod.Post, "/v1/auth/login",
            new { email = auditorEmail, password = "denetci-parola-1" });
        var finance = await SendOk<Tokens>(HttpMethod.Post, "/v1/auth/login",
            new { email = financeEmail, password = "finans-parola-11" });

        // Denetçi: finansman oranını değiştiremez
        var auditorSettings = await Send(HttpMethod.Post, "/v1/ledger/settings",
            new { annualFinancingRateBps = 3500 }, ("Authorization", $"Bearer {auditor.AccessToken}"));
        auditorSettings.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await auditorSettings.Content.ReadAsStringAsync()).ShouldContain("insufficient_role");

        // ... ekstre de yükleyemez. Rol reddi uca hiç inmediği için gövdenin
        // multipart olmaması sorun değildir — 403 middleware'den döner.
        (await Send(HttpMethod.Post, "/v1/settlements/upload", new { },
            ("Authorization", $"Bearer {auditor.AccessToken}"))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        // Finans TAM eşiktir (Admin değil): oranı değiştirebilir...
        (await Send(HttpMethod.Post, "/v1/ledger/settings",
            new { annualFinancingRateBps = 3500 },
            ("Authorization", $"Bearer {finance.AccessToken}"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // ... ve ekstre yükleyebilir
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(
            "value_date;amount;description;reference\n2026-08-13;100000;POS HASILAT;REF1\n"));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        body.Add(file, "file", "hesap-ekstresi.csv");
        body.Add(new StringContent(account.Id.ToString()), "connectorAccountId");
        body.Add(new StringContent("poyra_csv"), "format");

        var upload = new HttpRequestMessage(HttpMethod.Post, "/v1/settlements/upload") { Content = body };
        upload.Headers.Add("Authorization", $"Bearer {finance.AccessToken}");
        var uploaded = await _client.SendAsync(upload);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.OK, await uploaded.Content.ReadAsStringAsync());

        // Kimliksiz istek rol denetimine değil işyeri zorunluluğuna takılmalı (401; 500 değil)
        var anonymous = await Send(HttpMethod.Post, "/v1/ledger/settings",
            new { annualFinancingRateBps = 100 });
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.Content.ReadAsStringAsync()).ShouldContain("tenant_required");

        (await Send(HttpMethod.Get, "/v1/receivables", null)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ayni_eposta_ikinci_isyerinde_uyelik_alir_login_slug_ister()
    {
        var email = $"coklu-{Guid.NewGuid():N}@ornek.com";
        var tenantA = await CreateTenantWithOwnerAsync(email, "cok-gizli-parola-1");
        var tenantB = await CreateTenantWithOwnerAsync(email, "farkli-ama-onemsiz-1"); // mevcut kullanıcıya üyelik

        // Slug'sız giriş → 409 tenant_required (adaylar listelenir)
        var ambiguous = await Send(HttpMethod.Post, "/v1/auth/login",
            new { email, password = "cok-gizli-parola-1" });
        ambiguous.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ambiguous.Content.ReadAsStringAsync()).ShouldContain(tenantA.Slug);

        // Slug ile giriş B işyerine
        var tokens = await SendOk<Tokens>(HttpMethod.Post, "/v1/auth/login",
            new { email, password = "cok-gizli-parola-1", tenantSlug = tenantB.Slug });
        tokens.Tenant.Id.ShouldBe(tenantB.TenantId);
        tokens.Role.ShouldBe("owner");
    }
    [Fact]
    public async Task Turkce_buyuk_I_ile_kaydolan_kullanici_giris_YAPABILMELI()
    {
        const string password = "cok-gizli-parola-1";
        var ownerEmail = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await CreateTenantWithOwnerAsync(ownerEmail, password);

        // Gerçek senaryo: kullanıcı adresini büyük harfle yazarak kaydolur.
        // .NET'te "İ".ToLowerInvariant() harfi DEĞİŞTİRMEZ; katlama olmadan
        // kayıt "İbrahim@..." olarak saklanır ve kişi bir daha GİREMEZ.
        var email = $"İbrahim-{Guid.NewGuid():N}@ornek.com";
        await SendOk<object>(HttpMethod.Post, "/v1/users",
            new { email, password, displayName = "İbrahim", role = "operations" },
            ("X-Api-Key", tenant.ApiKey));

        // Girişte küçük harfle yazıyor
        var lower = email.Replace('İ', 'i').ToLowerInvariant();
        lower.ShouldNotBe(email);

        var login = await Send(HttpMethod.Post, "/v1/auth/login",
            new { email = lower, password, tenantSlug = tenant.Slug });

        login.StatusCode.ShouldBe(HttpStatusCode.OK,
            "Türkçe büyük İ ile kaydolan kullanıcı küçük harfle giriş yapabilmeli.");

        // Ters yön de tutmalı: küçükle kaydolup büyükle giren
        var second = $"ibrahim2-{Guid.NewGuid():N}@ornek.com";
        await SendOk<object>(HttpMethod.Post, "/v1/users",
            new { email = second, password, displayName = "İbrahim 2", role = "operations" },
            ("X-Api-Key", tenant.ApiKey));

        var upper = await Send(HttpMethod.Post, "/v1/auth/login",
            new { email = second.Replace("ibrahim2", "İbrahim2"), password, tenantSlug = tenant.Slug });
        upper.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

}
