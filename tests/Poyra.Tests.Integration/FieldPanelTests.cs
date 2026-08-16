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
/// Saha ekranı (M17 · panel). Ekranın işi tek bir ayrımı görünür kılmaktır:
/// <b>nakit beyanı tahsilat değildir</b>. Bunu karıştıran bir ekran, işyerine hiç
/// görmediği parayı toplanmış gibi gösterir.
///
/// Ayrıca rol denetimi ARAYÜZDE VE SUNUCUDA sınanır: butonu gizlemek yetmez.
/// </summary>
[Collection("postgres")]
public sealed class FieldPanelTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "saha-panel-1234";

    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory;
    private readonly WebApplicationFactory<PanelEntryPoint> _panelFactory;
    private readonly HttpClient _api;

    public FieldPanelTests(PostgresFixture fixture)
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
            builder.UseSetting("Poyra:CheckoutBaseUrl", "http://localhost");
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
    private sealed record UserRow(Guid Id, string Email, string Role);

    private HttpClient Panel()
        => _panelFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<T> ApiOk<T>(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);

        var response = await _api.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(TenantCreated Tenant, string OwnerEmail)> SeedAsync()
    {
        var email = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await ApiOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Saha Panel A.Ş.",
            slug = "shp-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "Sahip",
        }, ("X-Platform-Key", AdminKey));

        return (tenant, email);
    }

    private static async Task<HttpClient> LoginAsync(HttpClient panel, string email, string slug)
    {
        var response = await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = Password,
            ["tenantSlug"] = slug,
        }));
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return panel;
    }

    private static object Op(string method, long amount) => new
    {
        clientOpId = Guid.NewGuid(),
        method,
        amountMinor = amount,
        currency = "TRY",
        // Gerçek bir TR cihazının gönderdiği gibi +03:00
        capturedAtDevice = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)),
        customerRef = (string?)null,
        description = (string?)null,
        note = (string?)null,
        latitude = (double?)null,
        longitude = (double?)null,
    };

    // ------------------------------------------------------------------ ekran

    [Fact]
    public async Task Ekran_NAKIT_ile_tahsilati_AYRI_gostermeli()
    {
        var (tenant, email) = await SeedAsync();

        await ApiOk<object>(HttpMethod.Post, "/v1/field/agents",
            new { code = "BAYI-01", name = "Ahmet Yılmaz", region = "İstanbul Avrupa" },
            ("X-Api-Key", tenant.ApiKey));

        await ApiOk<object>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "BAYI-01",
            deviceId = "cihaz-A",
            operations = new[]
            {
                Op("cash_declared", 150_000), // 1.500,00 ₺ nakit BEYAN
                Op("link", 89_950),           // 899,50 ₺ bağlantı — henüz ödenmedi
            },
        }, ("X-Api-Key", tenant.ApiKey));

        var panel = await LoginAsync(Panel(), email, tenant.Slug);
        var html = await panel.GetStringAsync("/saha");

        // Nakit ve tahsilat AYRI görünür — birleştirmek hiç görmediğimiz parayı
        // toplanmış göstermek olurdu
        html.ShouldContain("Nakit beyanı");
        html.ShouldContain("1.500,00 ₺");
        html.ShouldContain("kasaya teslim edilmeli");

        // Bağlantı üretildi ama ödenmedi → 'bekleyen', 'kesinleşen' değil
        html.ShouldContain("899,50 ₺");
        html.ShouldContain("Bağlantı gönderildi");

        // Temsilci ve bölge görünür
        html.ShouldContain("bayi-01");
        html.ShouldContain("İstanbul Avrupa");

        // Kuruş gizlenmez
        html.ShouldNotContain("1.500 ₺");
    }

    [Fact]
    public async Task Ekran_cihaz_beyanini_sunucu_zamanindan_AYIRMALI()
    {
        var (tenant, email) = await SeedAsync();

        await ApiOk<object>(HttpMethod.Post, "/v1/field/agents",
            new { code = "BAYI-02", name = "Ayşe Demir", region = "Ege" },
            ("X-Api-Key", tenant.ApiKey));

        await ApiOk<object>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "BAYI-02",
            deviceId = "cihaz-B",
            operations = new[]
            {
                new
                {
                    clientOpId = Guid.NewGuid(),
                    method = "cash_declared",
                    amountMinor = 25_000L,
                    currency = "TRY",
                    // Fabrika ayarına dönmüş telefon
                    capturedAtDevice = DateTimeOffset.UnixEpoch,
                    customerRef = (string?)null,
                    description = (string?)null,
                    note = (string?)null,
                    latitude = (double?)null,
                    longitude = (double?)null,
                },
            },
        }, ("X-Api-Key", tenant.ApiKey));

        var panel = await LoginAsync(Panel(), email, tenant.Slug);
        var html = await panel.GetStringAsync("/saha");

        // İki zaman AYRI kolonda; beyan düzeltilmemiş
        html.ShouldContain("Cihaz beyanı");
        html.ShouldContain("Sunucu (yasal)");
        html.ShouldContain("01.01.1970");

        // Ve sapma sayılmış — denetim nereye bakacağını bilsin
        html.ShouldContain("Saati bozuk kayıt");
        html.ShouldContain("gün");
    }

    // ------------------------------------------------------------------ rol denetimi

    [Fact]
    public async Task Operations_kullanicisi_temsilci_ACAMAMALI()
    {
        var (tenant, ownerEmail) = await SeedAsync();

        var operationsEmail = $"op-{Guid.NewGuid():N}@ornek.com";
        await ApiOk<UserRow>(HttpMethod.Post, "/v1/users", new
        {
            email = operationsEmail,
            password = Password,
            displayName = "Operasyon",
            role = "operations",
        }, ("X-Api-Key", tenant.ApiKey));

        var panel = await LoginAsync(Panel(), operationsEmail, tenant.Slug);

        // Arayüzde form YOK — kimin şirket adına para toplayacağı yönetim kararıdır
        var html = await panel.GetStringAsync("/saha");
        html.ShouldNotContain("Temsilci ekle");
        html.ShouldContain("admin");

        // Ve doğrudan POST etse de SUNUCU reddeder: butonu gizlemek koruma değildir
        var forced = await panel.PostAsync("/saha/temsilci", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["code"] = "KACAK-01", ["name"] = "Kaçak" }));

        (await PanelRedirects.RevealAsync(panel, forced))
            .ShouldContain("admin");

        // Gerçekten açılmadığını sahibin ekranından doğrula
        var ownerPanel = await LoginAsync(Panel(), ownerEmail, tenant.Slug);
        (await ownerPanel.GetStringAsync("/saha")).ShouldNotContain("kacak-01");
    }

    [Fact]
    public async Task Admin_temsilci_acabilmeli_ve_cihazi_birakabilmeli()
    {
        var (tenant, email) = await SeedAsync();
        var panel = await LoginAsync(Panel(), email, tenant.Slug);

        var created = await panel.PostAsync("/saha/temsilci", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["code"] = "İZMİR-3", // Türkçe 'İ' katlanmalı
                ["name"] = "Mehmet Kaya",
                ["region"] = "Ege",
            }));
        (await PanelRedirects.RevealAsync(panel, created)).ShouldContain("izmir-3");

        // Cihazı bağla (ilk senkron)
        await ApiOk<object>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "izmir-3",
            deviceId = "SM-A546B-01",
            operations = new[] { Op("cash_declared", 5_000) },
        }, ("X-Api-Key", tenant.ApiKey));

        var html = await panel.GetStringAsync("/saha");
        html.ShouldContain("SM-A546B-01");
        html.ShouldContain("Cihazı bırak");

        // Telefon değişimi yönetici kararıdır
        var agents = await ApiOk<List<AgentRow>>(HttpMethod.Get, "/v1/field/agents", null,
            ("X-Api-Key", tenant.ApiKey));
        var agent = agents.Single(a => a.Code == "izmir-3");

        var released = await panel.PostAsync($"/saha/temsilci/{agent.Id}/cihaz-birak",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["reason"] = "Telefon değişti" }));
        (await PanelRedirects.RevealAsync(panel, released)).ShouldContain("bırakıldı");

        (await panel.GetStringAsync("/saha")).ShouldContain("bağlı değil");
    }

    [Fact]
    public async Task Kapatilan_temsilci_ekranda_KALMALI()
    {
        var (tenant, email) = await SeedAsync();
        var panel = await LoginAsync(Panel(), email, tenant.Slug);

        await panel.PostAsync("/saha/temsilci", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["code"] = "AYRILAN-1", ["name"] = "Ayrılan Kişi" }));

        var agents = await ApiOk<List<AgentRow>>(HttpMethod.Get, "/v1/field/agents", null,
            ("X-Api-Key", tenant.ApiKey));
        var agent = agents.Single(a => a.Code == "ayrilan-1");

        await panel.PostAsync($"/saha/temsilci/{agent.Id}/kapat",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["reason"] = "İşten ayrıldı" }));

        // İLKE 3: silinmez. "Bu parayı kim topladı" sorusu yıllar sonra da sorulur.
        var html = await panel.GetStringAsync("/saha");
        html.ShouldContain("ayrilan-1");
        html.ShouldContain("kapatıldı");
    }

    private sealed record AgentRow(Guid Id, string Code, string Name, string? Region, string? DeviceId);
}
