using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Poyra.Panel;
using Poyra.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F6.2 TR Karekod: müşteri kendi banka uygulamasıyla okutup öder. Poyra standart yükü
/// kurar; TANIMLAYICILAR BANKADAN gelir. Eksik ayarla üretilen karekod geçersizdir ve
/// müşteri kasada telefonu boşuna tutar — bu yüzden yarım ayarda üretim REDDEDİLİR.
/// </summary>
[Collection("postgres")]
public sealed class KarekodTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "karekod-parola-123";

    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory;
    private readonly WebApplicationFactory<PanelEntryPoint> _panelFactory;
    private readonly HttpClient _api;

    public KarekodTests(PostgresFixture fixture)
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
            builder.UseSetting("Poyra:PanelBaseUrl", "http://localhost");
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
    private sealed record LinkDto(string Id, string Slug, long? AmountMinor);
    private sealed record KarekodSettingsDto(
        bool Configured, string? SchemeGuid, string? MerchantNo,
        string? CategoryCode, string? MerchantName, string? MerchantCity);

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
        var email = $"karekod-{Guid.NewGuid():N}@ornek.com";
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Karekod A.Ş.",
            slug = "karekod-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "Karekod Kullanıcısı",
        }, ("X-Platform-Key", AdminKey));
        return (tenant, email);
    }

    private Task ConfigureKarekodAsync(string apiKey) => SendOk<KarekodSettingsDto>(
        HttpMethod.Post, "/v1/karekod/settings", new
        {
            schemeGuid = "TR.TEST.SEMA",
            merchantNo = "000000012345678",
            categoryCode = "5411",
            merchantName = "Şahin Mobilya",
            merchantCity = "İstanbul",
        }, ("X-Api-Key", apiKey));

    private Task<LinkDto> CreateLinkAsync(string apiKey, object body)
        => SendOk<LinkDto>(HttpMethod.Post, "/v1/payment-links", body, ("X-Api-Key", apiKey));

    // ---- Senaryolar ----------------------------------------------------------

    [Fact]
    public async Task Ayar_eksikken_karekod_uretilmemeli()
    {
        var (tenant, _) = await SeedTenantAsync();
        var link = await CreateLinkAsync(tenant.ApiKey,
            new { description = "Ayarsız", amountMinor = 10_000 });

        var response = await Send(HttpMethod.Get, $"/v1/payment-links/{link.Id}/karekod", null,
            ("X-Api-Key", tenant.ApiKey));

        // Yarım ayarla "çalışıyormuş gibi" QR basmak en kötü sonuçtur
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("karekod.not_configured");
        body.ShouldContain("bankanızdan");
    }

    [Fact]
    public async Task Sabit_tutarli_link_dinamik_karekod_uretmeli()
    {
        var (tenant, _) = await SeedTenantAsync();
        await ConfigureKarekodAsync(tenant.ApiKey);
        var link = await CreateLinkAsync(tenant.ApiKey,
            new { description = "Koltuk takımı", amountMinor = 1_499_00 });

        var response = await Send(HttpMethod.Get, $"/v1/payment-links/{link.Id}/karekod", null,
            ("X-Api-Key", tenant.ApiKey));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();

        // Yapı: EMVCo TLV + CRC. Sağlama tutmuyorsa banka uygulaması yükü reddeder.
        EmvQr.Verify(payload).ShouldBeTrue($"karekod CRC'si tutmalı:\n{payload}");
        payload.ShouldStartWith("000201");
        payload.ShouldContain("010212");           // dinamik (tutar yükte)
        payload.ShouldContain("5303949");          // TRY
        payload.ShouldContain("54071499.00");      // tutar: nokta ondalık, binlik ayracı yok (7 karakter → 5407)
        payload.ShouldContain("5802TR");
        payload.ShouldContain("52045411");         // MCC
        payload.ShouldContain("TR.TEST.SEMA");
        payload.ShouldContain("000000012345678");
        // Referans: bağlantı slug'ı (EMVCo referans alanı 25 karakter; lnk_… kimliği 36'dır
        // ve kırpılınca eşleşmez). Dekontta görünür, mutabakat bununla eşler.
        payload.ShouldContain(link.Slug);

        // Türkçe harfler ASCII'ye indirgenir: çok baytlı karakter yükü kaydırır
        payload.ShouldContain("Sahin Mobilya");
        payload.ShouldContain("Istanbul");
        payload.ShouldNotContain("Şahin");
    }

    [Fact]
    public async Task Acik_tutarli_link_statik_karekod_uretmeli()
    {
        var (tenant, _) = await SeedTenantAsync();
        await ConfigureKarekodAsync(tenant.ApiKey);
        var link = await CreateLinkAsync(tenant.ApiKey, new { description = "Bağış" });

        var payload = await (await Send(HttpMethod.Get, $"/v1/payment-links/{link.Id}/karekod", null,
            ("X-Api-Key", tenant.ApiKey))).Content.ReadAsStringAsync();

        EmvQr.Verify(payload).ShouldBeTrue();
        payload.ShouldContain("010211");   // statik: tutarı müşteri banka uygulamasında girer
        payload.ShouldNotContain("5406");  // tutar alanı YOK
        payload.ShouldNotContain("5405");
    }

    [Fact]
    public async Task Karekod_svg_olarak_da_donmeli()
    {
        var (tenant, _) = await SeedTenantAsync();
        await ConfigureKarekodAsync(tenant.ApiKey);
        var link = await CreateLinkAsync(tenant.ApiKey,
            new { description = "Masa 7", amountMinor = 48_500 });

        var response = await Send(HttpMethod.Get, $"/v1/payment-links/{link.Id}/karekod.svg", null,
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
        var svg = await response.Content.ReadAsStringAsync();
        svg.ShouldStartWith("<svg");
        svg.ShouldContain("crispEdges");
    }

    [Fact]
    public async Task Gecersiz_mcc_reddedilmeli()
    {
        var (tenant, _) = await SeedTenantAsync();

        var response = await Send(HttpMethod.Post, "/v1/karekod/settings",
            new { schemeGuid = "TR.TEST.SEMA", merchantNo = "123", categoryCode = "market" },
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("MCC");
    }

    [Fact]
    public async Task Karekod_ayarlari_isyerleri_arasi_sizmamali()
    {
        var (tenantA, _) = await SeedTenantAsync();
        var (tenantB, _) = await SeedTenantAsync();
        await ConfigureKarekodAsync(tenantA.ApiKey);

        var settingsB = await SendOk<KarekodSettingsDto>(HttpMethod.Get, "/v1/karekod/settings", null,
            ("X-Api-Key", tenantB.ApiKey));

        settingsB.Configured.ShouldBeFalse();
        settingsB.MerchantNo.ShouldBeNull();

        // A'nın bağlantısının karekodu B'ye kapalı
        var linkA = await CreateLinkAsync(tenantA.ApiKey,
            new { description = "A ürünü", amountMinor = 5_000 });
        (await Send(HttpMethod.Get, $"/v1/payment-links/{linkA.Id}/karekod", null,
            ("X-Api-Key", tenantB.ApiKey))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Panelden_karekod_ayarlanip_indirilebilmeli()
    {
        var (tenant, email) = await SeedTenantAsync();
        var panel = _panelFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = Password, ["tenantSlug"] = tenant.Slug,
        }))).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // Ayarsızken uyarı görünür
        (await panel.GetStringAsync("/ayarlar")).ShouldContain("Karekod henüz kullanılamıyor");

        var save = await panel.PostAsync("/ayarlar/karekod",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["schemeGuid"] = "TR.TEST.SEMA",
                ["merchantNo"] = "000000099887766",
                ["categoryCode"] = "5812",
                ["merchantName"] = "Panel Lokanta",
                ["merchantCity"] = "Ankara",
            }));
        save.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await PanelRedirects.RevealAsync(panel, save)).ShouldContain("test setiyle doğrulayın");

        var page = await panel.GetStringAsync("/ayarlar");
        page.ShouldNotContain("Karekod henüz kullanılamıyor");
        page.ShouldContain("000000099887766");

        var link = await CreateLinkAsync(tenant.ApiKey,
            new { description = "Menü", amountMinor = 32_000 });
        (await panel.GetStringAsync("/baglantilar-odeme")).ShouldContain("TR Karekod");

        var karekod = await panel.GetAsync($"/baglantilar-odeme/{link.Id}/karekod.svg");
        karekod.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await karekod.Content.ReadAsStringAsync()).ShouldStartWith("<svg");
    }

    [Fact]
    public async Task Ayarsiz_isyerinde_panel_karekod_indirmeyi_anlasilir_reddetmeli()
    {
        var (tenant, email) = await SeedTenantAsync();
        var link = await CreateLinkAsync(tenant.ApiKey,
            new { description = "Ayarsız", amountMinor = 1_000 });

        var panel = _panelFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = Password, ["tenantSlug"] = tenant.Slug,
        }));

        var response = await panel.GetAsync($"/baglantilar-odeme/{link.Id}/karekod.svg");

        // Bozuk SVG yerine açıklamalı yönlendirme
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await PanelRedirects.RevealAsync(panel, response)).ShouldContain("bankanızdan");
    }
}
