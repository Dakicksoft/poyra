using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Poyra.Panel;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F5.2: işyeri POS hesabını panelden kurar — anahtar yenileme, elle yoklama, öncelik/mod
/// ayarı ve devre dışı bırakma. Hiçbir akışta hesap SİLİNMEZ (İlke 3); kimlikler geri okunamaz.
/// </summary>
[Collection("postgres")]
public sealed class ConnectorManagementTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "pos-parola-123";

    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory;
    private readonly WebApplicationFactory<PanelEntryPoint> _panelFactory;
    private readonly HttpClient _api;

    public ConnectorManagementTests(PostgresFixture fixture)
    {
        void Configure(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Panel:Antiforgery:Enforce", "false"); // form token kurulumu ayrı testte
            builder.UseSetting("Platform:AdminKey", AdminKey);
            builder.UseSetting("Poyra:PublicBaseUrl", "http://localhost");
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Poyra:JwtKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            builder.UseSetting("Poyra:VaultKey", Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
        }

        _apiFactory = new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(Configure);
        _panelFactory = new WebApplicationFactory<PanelEntryPoint>().WithWebHostBuilder(Configure);
        _api = _apiFactory.CreateClient();
    }

    public void Dispose()
    {
        _apiFactory.Dispose();
        _panelFactory.Dispose();
    }

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record AccountDto(
        Guid Id, string ConnectorKey, string Label, int Priority, bool TestMode, string Status, string Health);
    private sealed record ProbeDto(Guid AccountId, string Label, bool Supported, bool Healthy, string? Detail);
    private sealed record LoginDto(string AccessToken);

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

    private async Task<(TenantCreated Tenant, string Email)> SeedTenantAsync()
    {
        var email = $"pos-{Guid.NewGuid():N}@ornek.com";
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "POS Kurulumu A.Ş.",
            slug = "pos-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "POS Kullanıcısı",
        }, ("X-Platform-Key", AdminKey));
        return (tenant, email);
    }

    private async Task<HttpClient> LoginPanelAsync(string email, string tenantSlug)
    {
        var panel = _panelFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = Password,
            ["tenantSlug"] = tenantSlug,
        }));
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return panel;
    }

    // ---- Senaryolar ----------------------------------------------------------

    [Fact]
    public async Task Katalog_kimlik_alan_semasini_vermeli()
    {
        var (tenant, _) = await SeedTenantAsync();

        var catalog = await SendOk<List<Dictionary<string, object>>>(
            HttpMethod.Get, "/v1/connectors/catalog", null, ("X-Api-Key", tenant.ApiKey));

        // Panel formu bu şemadan üretilir — alansız katalog girdisi form üretemez
        catalog.ShouldNotBeEmpty();
        var json = System.Text.Json.JsonSerializer.Serialize(catalog);
        json.ShouldContain("credentialFields");
        json.ShouldContain("secret");
    }

    [Fact]
    public async Task Eksik_kimlik_alani_400_donmeli()
    {
        var (tenant, _) = await SeedTenantAsync();

        var response = await Send(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "nestpay",
            label = "Eksik NestPay",
            credentials = new Dictionary<string, string> { ["clientId"] = "700123" },
        }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Eksik kimlik alanları");
    }

    [Fact]
    public async Task Anahtar_yenileme_hesabi_korumali_ve_baglantiyi_sinamali()
    {
        var (tenant, _) = await SeedTenantAsync();

        var account = await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        // Yanlış anahtar: yenileme sonrası sağlık HEMEN düşmeli (canary beklenmez)
        var broken = await SendOk<AccountDto>(HttpMethod.Post,
            $"/v1/connector-accounts/{account.Id}/credentials",
            new { credentials = new Dictionary<string, string> { ["secret"] = "s3cret", ["fail_initiate"] = "true" } },
            ("X-Api-Key", tenant.ApiKey));

        broken.Id.ShouldBe(account.Id); // hesap kimliği SABİT — geçmiş işlemler bağlı kalır
        broken.Health.ShouldBe("down");

        // Doğru anahtar geri yazılınca sağlık toparlar
        var fixedAccount = await SendOk<AccountDto>(HttpMethod.Post,
            $"/v1/connector-accounts/{account.Id}/credentials",
            new { credentials = new Dictionary<string, string> { ["secret"] = "yeni-s3cret" } },
            ("X-Api-Key", tenant.ApiKey));
        fixedAccount.Health.ShouldBe("healthy");

        // Yeni anahtarla ödeme akışı çalışmaya devam eder
        var payment = await SendOk<Dictionary<string, object>>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 5_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        payment["status"].ToString().ShouldBe("requires_action");
    }

    [Fact]
    public async Task Elle_yoklama_sagligi_guncellemeli()
    {
        var (tenant, _) = await SeedTenantAsync();
        var account = await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Yoklanacak POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret", ["fail_initiate"] = "true" },
        }, ("X-Api-Key", tenant.ApiKey));

        var probe = await SendOk<ProbeDto>(HttpMethod.Post,
            $"/v1/connector-accounts/{account.Id}/probe", null, ("X-Api-Key", tenant.ApiKey));

        probe.Supported.ShouldBeTrue();
        probe.Healthy.ShouldBeFalse();

        var accounts = await SendOk<List<AccountDto>>(HttpMethod.Get, "/v1/connector-accounts", null,
            ("X-Api-Key", tenant.ApiKey));
        accounts.Single(a => a.Id == account.Id).Health.ShouldBe("down");
    }

    [Fact]
    public async Task Ayar_degisikligi_etiket_cakismasini_reddetmeli()
    {
        var (tenant, _) = await SeedTenantAsync();
        var first = await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Birinci POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
        }, ("X-Api-Key", tenant.ApiKey));
        var second = await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "İkinci POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
        }, ("X-Api-Key", tenant.ApiKey));

        // Etiket rota kuralında ADRES: çakışırsa kural yanlış POS'a yollar
        var conflict = await Send(HttpMethod.Post, $"/v1/connector-accounts/{second.Id}/settings",
            new { label = first.Label }, ("X-Api-Key", tenant.ApiKey));
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var updated = await SendOk<AccountDto>(HttpMethod.Post,
            $"/v1/connector-accounts/{second.Id}/settings",
            new { label = "İkinci POS (canlı)", priority = 5, testMode = false },
            ("X-Api-Key", tenant.ApiKey));
        updated.Label.ShouldBe("İkinci POS (canlı)");
        updated.Priority.ShouldBe(5);
        updated.TestMode.ShouldBeFalse();
    }

    [Fact]
    public async Task Panelden_hesap_eklenip_yoklanip_devre_disi_birakilmali()
    {
        var (tenant, email) = await SeedTenantAsync();
        var panel = await LoginPanelAsync(email, tenant.Slug);

        // Katalog seçimi sayfada dinamik form üretir
        var page = await panel.GetStringAsync("/baglantilar?konnektor=mockbank");
        page.ShouldContain("Yeni bağlantı hesabı");
        page.ShouldContain("cred_secret"); // katalog şemasından üretilen alan

        var create = await panel.PostAsync("/baglantilar/ekle",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["connectorKey"] = "mockbank",
                ["label"] = "Panelden Eklenen POS",
                ["priority"] = "7",
                ["cred_secret"] = "panel-s3cret",
                ["testMode"] = "true",
            }));
        create.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        create.Headers.Location!.ToString().ShouldContain("sonuc=");

        var list = await panel.GetStringAsync("/baglantilar");
        list.ShouldContain("Panelden Eklenen POS");
        list.ShouldContain("Test et");

        var accounts = await SendOk<List<AccountDto>>(HttpMethod.Get, "/v1/connector-accounts", null,
            ("X-Api-Key", tenant.ApiKey));
        var account = accounts.Single(a => a.Label == "Panelden Eklenen POS");
        account.Priority.ShouldBe(7);

        // "Test et" → sağlık yazılır
        var probe = await panel.PostAsync($"/baglantilar/{account.Id}/test", new FormUrlEncodedContent([]));
        (await PanelRedirects.RevealAsync(panel, probe)).ShouldContain("bankaya ulaşıyor ✓");

        // Devre dışı bırak → rota artık bu POS'u aday saymaz
        var disable = await panel.PostAsync($"/baglantilar/{account.Id}/durum",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["status"] = "disabled" }));
        disable.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        (await SendOk<List<AccountDto>>(HttpMethod.Get, "/v1/connector-accounts", null,
            ("X-Api-Key", tenant.ApiKey)))
            .Single(a => a.Id == account.Id).Status.ShouldBe("disabled");
    }

    [Fact]
    public async Task Auditor_pos_hesabi_ekleyememeli()
    {
        var (tenant, ownerEmail) = await SeedTenantAsync();

        // Sahip, denetçi kullanıcı açar
        var auditorEmail = $"denetci-{Guid.NewGuid():N}@ornek.com";
        var owner = await LoginPanelAsync(ownerEmail, tenant.Slug);
        owner.Dispose();

        var token = await SendOk<LoginDto>(HttpMethod.Post, "/v1/auth/login",
            new { email = ownerEmail, password = Password, tenantSlug = tenant.Slug }, []);
        await SendOk<Dictionary<string, object>>(HttpMethod.Post, "/v1/users",
            new { email = auditorEmail, password = Password, displayName = "Denetçi", role = "auditor" },
            ("Authorization", $"Bearer {token.AccessToken}"));

        var auditor = await LoginPanelAsync(auditorEmail, tenant.Slug);
        var attempt = await auditor.PostAsync("/baglantilar/ekle",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["connectorKey"] = "mockbank",
                ["label"] = "Denetçinin POS'u",
                ["cred_secret"] = "s3cret",
            }));

        // Arayüzde formu göstermemek yetmez — sunucu da reddetmeli
        attempt.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        attempt.Headers.Location!.ToString().ShouldContain("hata=");

        (await SendOk<List<AccountDto>>(HttpMethod.Get, "/v1/connector-accounts", null,
            ("X-Api-Key", tenant.ApiKey))).ShouldBeEmpty();
    }
}
