using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Poyra.Panel;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// CSRF (antiforgery) zorlaması: panel form POST'ları belirteçsiz KABUL EDİLMEZ.
/// Diğer entegrasyon testleri zorlamayı kapatır (konuları form kurulumu değildir);
/// zorlamanın kendisi burada, varsayılan (açık) yapılandırmayla kanıtlanır.
/// </summary>
[Collection("postgres")]
public sealed class AntiforgeryTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "panel-parola-123";
    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory;
    private readonly WebApplicationFactory<PanelEntryPoint> _panelFactory;
    private readonly HttpClient _api;

    public AntiforgeryTests(PostgresFixture fixture)
    {
        void Configure(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            // DİKKAT: Panel:Antiforgery:Enforce BİLEREK ayarlanmıyor — varsayılan (zorla) test edilir
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

    private async Task<(string Email, string Slug)> SeedTenantAsync()
    {
        var email = $"csrf-{Guid.NewGuid():N}@ornek.com";
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/tenants")
        {
            Content = JsonContent.Create(new
            {
                name = "CSRF İşyeri",
                slug = "csrf-" + Guid.NewGuid().ToString("N")[..10],
                ownerEmail = email,
                ownerPassword = Password,
                ownerName = "Sahip",
            }),
        };
        request.Headers.Add("X-Platform-Key", AdminKey);
        var response = await _api.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var tenant = (await response.Content.ReadFromJsonAsync<TenantCreated>())!;
        return (email, tenant.Slug);
    }

    /// <summary>Sayfadaki gizli antiforgery alanını çeker (çerez istemcide birikir).</summary>
    private static async Task<string> GetTokenAsync(HttpClient panel, string page)
    {
        var html = await panel.GetStringAsync(page);
        var match = Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        match.Success.ShouldBeTrue($"{page} sayfasında antiforgery alanı bulunamadı");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task Belirtecsiz_giris_posti_reddedilip_dostane_yonlendirilmeli()
    {
        var (email, slug) = await SeedTenantAsync();
        var panel = _panelFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await panel.PostAsync("/giris", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = Password,
                ["tenantSlug"] = slug,
            }));

        // Oturum AÇILMAMALI: filtre dostane redirect döndürür, Set-Cookie'de oturum çerezi olmaz
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join(";", values) : "";
        setCookies.ShouldNotContain(".AspNetCore.Cookies=");
    }

    [Fact]
    public async Task Belirtecli_giris_posti_kabul_edilmeli()
    {
        var (email, slug) = await SeedTenantAsync();
        var panel = _panelFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetTokenAsync(panel, "/giris");
        var response = await panel.PostAsync("/giris", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = Password,
                ["tenantSlug"] = slug,
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().ShouldBe("/");
    }

    [Fact]
    public async Task Belirtecsiz_panel_aksiyonu_calismamali_belirteclisi_calismali()
    {
        var (email, slug) = await SeedTenantAsync();
        var panel = _panelFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Belirteçli girişle oturum aç
        var loginToken = await GetTokenAsync(panel, "/giris");
        (await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = Password,
            ["tenantSlug"] = slug,
            ["__RequestVerificationToken"] = loginToken,
        }))).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // Belirteçsiz aksiyon: filtre işlemi ÇALIŞTIRMADAN geri gönderir (hata flash'lı)
        var forged = await panel.PostAsync("/musteriler/kaydet", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["ref"] = "sahte-musteri" }));
        forged.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        forged.Headers.Location!.ToString().ShouldContain("hata=");

        // Belirteçli aynı aksiyon: işlem gerçekleşir, sonuc flash'ıyla döner
        var actionToken = await GetTokenAsync(panel, "/musteriler");
        var genuine = await panel.PostAsync("/musteriler/kaydet", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["ref"] = "gercek-musteri",
                ["__RequestVerificationToken"] = actionToken,
            }));
        genuine.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        genuine.Headers.Location!.ToString().ShouldContain("sonuc=");
    }
}
