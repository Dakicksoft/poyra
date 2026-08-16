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
/// F3.2 Panel: kimlik kapısı, giriş akışı, ekranların gerçek veriyi göstermesi ve
/// işyeri izolasyonu. Statik SSR sayfaları GET'te tam HTML döner — içerik doğrulanabilir.
/// </summary>
[Collection("postgres")]
public sealed class PanelFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "panel-parola-123";
    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory; // veri hazırlığı API üzerinden
    private readonly WebApplicationFactory<PanelEntryPoint> _panelFactory;
    private readonly HttpClient _api;

    public PanelFlowTests(PostgresFixture fixture)
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
    private sealed record AccountDto(Guid Id);
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);

    /// <summary>Panel istemcisi: çerez taşır, yönlendirmeyi elle izleriz (kontrol için).</summary>
    private HttpClient CreatePanelClient()
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

    private async Task<(TenantCreated Tenant, string Email)> SeedTenantWithUserAsync(string label)
    {
        var email = $"panel-{Guid.NewGuid():N}@ornek.com";
        var tenant = await ApiOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = label,
            slug = "panel-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "Panel Kullanıcısı",
        }, ("X-Platform-Key", AdminKey));

        await ApiOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        return (tenant, email);
    }

    private async Task<string> RunSuccessfulPaymentAsync(string apiKey, long amountMinor)
    {
        var payment = await ApiOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor, currency = "TRY", confirm = true, description = "Panel testi" },
            ("X-Api-Key", apiKey));
        (await _api.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields))).StatusCode.ShouldBe(HttpStatusCode.OK);
        return payment.Id;
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient panel, string email, string slug)
        => await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = Password,
            ["tenantSlug"] = slug,
        }));

    [Fact]
    public async Task Kimliksiz_istek_giris_sayfasina_yonlendirilmeli()
    {
        var panel = CreatePanelClient();

        // Blazor NotAuthorized dalı giriş sayfasına yönlendirir; sayfanın kendisi 200 döner
        // ama içerik giriş formudur (statik SSR).
        var response = await panel.GetAsync("/odemeler");
        var html = await response.Content.ReadAsStringAsync();

        html.ShouldNotContain("Ödemeler</h1>");

        // Giriş sayfası anonim erişilebilir olmalı
        var login = await panel.GetAsync("/giris");
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await login.Content.ReadAsStringAsync()).ShouldContain("Giriş yap");
    }

    [Fact]
    public async Task Yanlis_parola_hata_ile_geri_donmeli_dogru_parola_panele_almali()
    {
        var (tenant, email) = await SeedTenantWithUserAsync("Panel İşyeri");
        var panel = CreatePanelClient();

        var bad = await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "yanlis-parola",
            ["tenantSlug"] = tenant.Slug,
        }));
        bad.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        bad.Headers.Location!.ToString().ShouldContain("/giris?hata=");
        bad.Headers.TryGetValues("Set-Cookie", out _).ShouldBeFalse(); // oturum açılmadı

        var good = await LoginAsync(panel, email, tenant.Slug);
        good.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        good.Headers.Location!.ToString().ShouldBe("/");
        good.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith("poyra_panel="));
    }

    [Fact]
    public async Task Panel_gercek_odemeleri_ve_rota_gerekcesini_gostermeli()
    {
        var (tenant, email) = await SeedTenantWithUserAsync("Poyra Deneme A.Ş.");
        var paymentId = await RunSuccessfulPaymentAsync(tenant.ApiKey, 14_900);

        var panel = CreatePanelClient();
        await LoginAsync(panel, email, tenant.Slug);

        // Genel bakış: işyeri adı + tutar özeti
        var dashboard = await panel.GetStringAsync("/");
        dashboard.ShouldContain("Poyra Deneme A.Ş.");
        dashboard.ShouldContain("Genel Bakış");
        dashboard.ShouldContain("149,00"); // TR biçimi, kuruş gizlenmez

        // Ödeme listesi
        var list = await panel.GetStringAsync("/odemeler");
        list.ShouldContain(paymentId[..14]);
        list.ShouldContain("Başarılı"); // durum sözlüğü Türkçe

        // Detay: "neden bu POS" gerekçesi + zaman çizelgesi (silinemez olay defteri)
        var detail = await panel.GetStringAsync($"/odemeler/{paymentId}");
        detail.ShouldContain("Neden bu POS?");
        detail.ShouldContain("Öncelik sırası"); // rota kararının gerekçesi
        detail.ShouldContain("payment.succeeded"); // olay defteri
        detail.ShouldContain("Zaman çizelgesi");
    }

    [Fact]
    public async Task Panel_isyerleri_arasi_veri_sizdirmamali()
    {
        var (tenantA, emailA) = await SeedTenantWithUserAsync("İşyeri A");
        var (tenantB, emailB) = await SeedTenantWithUserAsync("İşyeri B");
        var paymentA = await RunSuccessfulPaymentAsync(tenantA.ApiKey, 33_300);

        var panelB = CreatePanelClient();
        await LoginAsync(panelB, emailB, tenantB.Slug);

        var listB = await panelB.GetStringAsync("/odemeler");
        listB.ShouldNotContain(paymentA[..14]);
        listB.ShouldContain("Görüntülenecek ödeme yok");

        // Doğrudan URL ile de erişilemez (RLS + global filtre)
        var detailB = await panelB.GetStringAsync($"/odemeler/{paymentA}");
        detailB.ShouldContain("Ödeme bulunamadı");
        detailB.ShouldNotContain("Zaman çizelgesi");

        // İşyeri A kendi kaydını görür
        var panelA = CreatePanelClient();
        await LoginAsync(panelA, emailA, tenantA.Slug);
        (await panelA.GetStringAsync("/odemeler")).ShouldContain(paymentA[..14]);
    }

    [Fact]
    public async Task Mutabakat_ekrani_komisyon_farkini_gostermeli()
    {
        var (tenant, email) = await SeedTenantWithUserAsync("Mutabakat İşyeri");
        var payment = await ApiOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        var orderId = payment.NextAction!.Fields["mb_order"];
        await _api.PostAsync(payment.NextAction.Url, new FormUrlEncodedContent(payment.NextAction.Fields));

        var accounts = await ApiOk<List<Dictionary<string, object>>>(
            HttpMethod.Get, "/v1/connector-accounts", null, ("X-Api-Key", tenant.ApiKey));
        var accountId = accounts[0]["id"].ToString();

        await ApiOk<object>(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = accountId, installmentCount = 1, rateBps = 200, valorDays = 1 },
            ("X-Api-Key", tenant.ApiKey));

        // Banka 250 kuruş kesmiş; beklenen 200 → +50 fazla kesim
        await ApiOk<object>(HttpMethod.Post, "/v1/recon/statements", new
        {
            connectorAccountId = accountId,
            statementDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)),
            lines = new object[]
            {
                new { orderId, grossMinor = 10_000, commissionMinor = 250, netMinor = 9_750 },
            },
        }, ("X-Api-Key", tenant.ApiKey));

        var panel = CreatePanelClient();
        await LoginAsync(panel, email, tenant.Slug);

        var list = await panel.GetStringAsync("/mutabakat");
        list.ShouldContain("Mutabakat");
        list.ShouldContain("0,50 ₺"); // komisyon farkı TR biçiminde

        // Rapor sayfası: itiraz edilebilir satır
        var statementId = ExtractStatementId(list);
        var report = await panel.GetStringAsync($"/mutabakat/{statementId}");
        report.ShouldContain("Komisyon denetimi");
        report.ShouldContain("fazla kesim — itiraz edilebilir");
        report.ShouldContain(orderId);
    }

    private static string ExtractStatementId(string html)
    {
        const string marker = "/mutabakat/";
        var index = html.IndexOf(marker + "0", StringComparison.Ordinal);
        if (index < 0)
            index = html.LastIndexOf(marker, StringComparison.Ordinal);
        index.ShouldBeGreaterThan(-1, "ekstre bağlantısı bulunamadı");
        return html.Substring(index + marker.Length, 36);
    }

    // ---- Oturumsuz sayfaların ortak kabuğu -----------------------------------------

    /// <summary>
    /// Giriş ekranı ikiye bölünür: solda marka ve ürünün ne yaptığını anlatan görsel,
    /// sağda form. Kabuk <c>AuthLayout</c>'tadır; bir sayfa yanlış layout'a bağlanırsa
    /// kullanıcı markasız, ortalanmamış bir form görür ve bunu kimse fark etmez.
    /// </summary>
    [Theory]
    [InlineData("/giris")]
    [InlineData("/parola-unuttum")]
    [InlineData("/parola-sifirla?token=x")]
    [InlineData("/eposta-dogrula?token=x")]
    public async Task Oturumsuz_sayfalar_ikiye_bolunmus_kabugu_kullanmali(string path)
    {
        var html = await CreatePanelClient().GetStringAsync(path);

        html.ShouldContain("auth-brand");   // sol: marka
        html.ShouldContain("auth-form");    // sağ: iş
        html.ShouldContain("hub-art");      // göbek çizimi
        html.ShouldContain("poyra");
    }

    [Fact]
    public async Task Giris_ekraninda_marka_IKI_KEZ_gorunmemeli()
    {
        // Marka solda bir kez durur. Sağ tarafta yinelemek, dar ekranda üst üste iki
        // logo demektir — eski düzenden kalan blok sayfada unutulursa bu test yakalar.
        var html = await CreatePanelClient().GetStringAsync("/giris");

        System.Text.RegularExpressions.Regex.Matches(html, "class=\"wordmark\"").Count.ShouldBe(1);
        html.ShouldNotContain("brand big");
    }

    [Fact]
    public async Task Giris_formu_ve_baglantilari_yerinde_olmali()
    {
        // Kabuk değişti; formun kendisi çalışmaya devam etmeli
        var html = await CreatePanelClient().GetStringAsync("/giris");

        html.ShouldContain("action=\"/giris\"");
        html.ShouldContain("name=\"email\"");
        html.ShouldContain("name=\"password\"");
        html.ShouldContain("name=\"tenantSlug\"");
        html.ShouldContain("/parola-unuttum");
    }
}
