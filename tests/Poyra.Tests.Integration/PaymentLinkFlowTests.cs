using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Api;
using Poyra.Checkout;
using Poyra.Panel;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F5.1 Ödeme bağlantısı + checkout: işyeri kendi ödeme sayfasını yazmadan tahsilat yapar.
/// Akış gerçek üç host üzerinden koşar — Api (link/ödeme/callback), Checkout (müşteriye
/// bakan kimliksiz sayfa), Panel (işyeri ekranı) — ve aynı RLS'li Postgres'e bağlanır.
/// </summary>
[Collection("postgres")]
public sealed class PaymentLinkFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "link-parola-123";

    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory;
    private readonly WebApplicationFactory<CheckoutEntryPoint> _checkoutFactory;
    private readonly WebApplicationFactory<PanelEntryPoint> _panelFactory;
    private readonly HttpClient _api;
    private readonly HttpClient _apiNoRedirect; // banka dönüşü 302 verir; kendimiz izleriz
    private readonly HttpClient _checkout;

    public PaymentLinkFlowTests(PostgresFixture fixture)
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
        _checkoutFactory = new WebApplicationFactory<CheckoutEntryPoint>().WithWebHostBuilder(Configure);
        _panelFactory = new WebApplicationFactory<PanelEntryPoint>().WithWebHostBuilder(Configure);
        _api = _apiFactory.CreateClient();
        _apiNoRedirect = _apiFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _checkout = _checkoutFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _apiFactory.Dispose();
        _checkoutFactory.Dispose();
        _panelFactory.Dispose();
    }

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record AccountDto(Guid Id);
    private sealed record LinkDto(
        string Id, string Slug, string Url, long? AmountMinor, string Currency, string Description,
        int MaxInstallments, DateTimeOffset? ExpiresAt, int MaxUsage, int SuccessCount, string Status);
    private sealed record PaymentDto(string Id, string Status);

    // ---- Yardımcılar ---------------------------------------------------------
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

    private async Task<(TenantCreated Tenant, string Email)> SeedTenantAsync(string label)
    {
        var email = $"link-{Guid.NewGuid():N}@ornek.com";
        var tenant = await ApiOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = label,
            slug = "link-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "Bağlantı Kullanıcısı",
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

    private Task<LinkDto> CreateLinkAsync(string apiKey, object body)
        => ApiOk<LinkDto>(HttpMethod.Post, "/v1/payment-links", body, ("X-Api-Key", apiKey));

    /// <summary>
    /// Checkout'ta "Güvenli ödemeye geç" → dönen otomatik-gönder formunu bankaya (Api'nin
    /// callback ucuna) post eder ve ödeme kimliğini döndürür.
    /// </summary>
    private async Task<string> PayThroughCheckoutAsync(
        string slug, decimal? amountLira = null, int? installments = null)
    {
        var form = new Dictionary<string, string>();
        if (amountLira is { } lira)
            form["amountLira"] = lira.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (installments is { } count)
            form["installments"] = count.ToString();

        var response = await _checkout.PostAsync($"/l/{slug}/ode", new FormUrlEncodedContent(form));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Bankanızın güvenli ödeme sayfasına yönlendiriliyorsunuz");

        // "Banka" formunu müşteri tarayıcısı gibi Api'ye gönder. Link akışında return_url
        // dolu olduğu için callback 302 ile checkout sonuç sayfasına yollar.
        var (url, fields) = ParseAutoSubmitForm(html);
        var callback = await _apiNoRedirect.PostAsync(url, new FormUrlEncodedContent(fields));
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await callback.Content.ReadAsStringAsync());

        var location = callback.Headers.Location!.ToString();
        location.ShouldContain($"/l/{slug}/sonuc");

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(location, UriKind.Absolute).Query);
        return query["poyra_payment_id"]!;
    }

    private static (string Url, Dictionary<string, string> Fields) ParseAutoSubmitForm(string html)
    {
        var action = Regex.Match(html, """<form id="f" method="[^"]+" action="([^"]+)">""");
        action.Success.ShouldBeTrue("otomatik gönder formu bulunamadı");

        var fields = Regex.Matches(html, """<input type="hidden" name="([^"]+)" value="([^"]*)" />""")
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value),
                          m => WebUtility.HtmlDecode(m.Groups[2].Value));

        return (WebUtility.HtmlDecode(action.Groups[1].Value), fields);
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
    public async Task Sabit_tutarli_link_ucdan_uca_tahsil_etmeli()
    {
        var (tenant, _) = await SeedTenantAsync("Bağlantı A.Ş.");
        var link = await CreateLinkAsync(tenant.ApiKey,
            new { description = "Danışmanlık bedeli", amountMinor = 149_00 });

        link.Id.ShouldStartWith("lnk_");
        link.Url.ShouldEndWith($"/l/{link.Slug}");

        // Müşteri sayfayı açar: açıklama + tutar TR biçiminde görünür
        var page = await _checkout.GetStringAsync($"/l/{link.Slug}");
        page.ShouldContain("Danışmanlık bedeli");
        page.ShouldContain("149,00 ₺"); // kuruş gizlenmez
        page.ShouldContain("Güvenli ödemeye geç");

        var paymentId = await PayThroughCheckoutAsync(link.Slug);

        // Sonuç sayfası durumu DEFTERDEN okur
        var result = await _checkout.GetStringAsync($"/l/{link.Slug}/sonuc?poyra_payment_id={paymentId}");
        result.ShouldContain("Ödemeniz alındı");
        result.ShouldContain("149,00 ₺");

        // Sayaç arttı
        var links = await ApiOk<List<LinkDto>>(HttpMethod.Get, "/v1/payment-links", null,
            ("X-Api-Key", tenant.ApiKey));
        links.Single(l => l.Id == link.Id).SuccessCount.ShouldBe(1);
    }

    [Fact]
    public async Task Sabit_tutarli_linkte_istemcinin_gonderdigi_tutar_yok_sayilmali()
    {
        var (tenant, _) = await SeedTenantAsync("Kurcalama Testi");
        var link = await CreateLinkAsync(tenant.ApiKey,
            new { description = "Sabit tutar", amountMinor = 250_00 });

        // Müşteri formu kurcalayıp 1 ₺ gönderiyor — sunucu linkteki tutarı kullanmalı
        var paymentId = await PayThroughCheckoutAsync(link.Slug, amountLira: 1m);

        var payment = await ApiOk<Dictionary<string, object>>(
            HttpMethod.Get, $"/v1/payments/{paymentId}", null, ("X-Api-Key", tenant.ApiKey));
        payment["amountMinor"].ToString().ShouldBe("25000");
    }

    [Fact]
    public async Task Acik_tutarli_link_musterinin_girdigi_tutari_kurusa_cevirmeli()
    {
        var (tenant, _) = await SeedTenantAsync("Bağış Kutusu");
        var link = await CreateLinkAsync(tenant.ApiKey, new { description = "Bağış" });

        link.AmountMinor.ShouldBeNull();
        (await _checkout.GetStringAsync($"/l/{link.Slug}")).ShouldContain("Tutar (₺)");

        var paymentId = await PayThroughCheckoutAsync(link.Slug, amountLira: 33.45m);

        var payment = await ApiOk<Dictionary<string, object>>(
            HttpMethod.Get, $"/v1/payments/{paymentId}", null, ("X-Api-Key", tenant.ApiKey));
        payment["amountMinor"].ToString().ShouldBe("3345");
    }

    [Fact]
    public async Task Sonuc_sayfasi_yenilense_de_kullanim_sayaci_bir_kez_artmali()
    {
        var (tenant, _) = await SeedTenantAsync("Tek Kullanımlık");
        var link = await CreateLinkAsync(tenant.ApiKey,
            new { description = "Tek kullanımlık bilet", amountMinor = 75_00, maxUsage = 1 });

        var paymentId = await PayThroughCheckoutAsync(link.Slug);

        // Müşteri sonuç sayfasını üç kez açıyor (yenile, geri/ileri)
        for (var i = 0; i < 3; i++)
            (await _checkout.GetStringAsync($"/l/{link.Slug}/sonuc?poyra_payment_id={paymentId}"))
                .ShouldContain("Ödemeniz alındı");

        var links = await ApiOk<List<LinkDto>>(HttpMethod.Get, "/v1/payment-links", null,
            ("X-Api-Key", tenant.ApiKey));
        links.Single(l => l.Id == link.Id).SuccessCount.ShouldBe(1);

        // Kullanım sınırı dolduğu için link artık ödeme kabul etmiyor
        var page = await _checkout.GetStringAsync($"/l/{link.Slug}");
        page.ShouldContain("kullanım sınırına ulaşmış");
        page.ShouldNotContain("Güvenli ödemeye geç");

        var blocked = await _checkout.PostAsync($"/l/{link.Slug}/ode", new FormUrlEncodedContent([]));
        blocked.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        blocked.Headers.Location!.ToString().ShouldContain("hata=");
    }

    [Fact]
    public async Task Musteri_sonuc_sayfasini_ACMASA_da_link_odenmis_sayilmali()
    {
        // GERÇEK HATA: kullanım kaydı yalnız müşteri sonuç sayfasını açtığında
        // yazılıyordu. Banka dönüşünden sonra sekmesini kapatan (ya da şebekesi kopan)
        // müşterinin PARASI ÇEKİLİYOR ama bağlantı "ödenmemiş" kalıyor ve tek
        // kullanımlık bağlantı İKİNCİ KEZ ödenebiliyordu.
        //
        // Paranın durumu, müşterinin tarayıcısının bir sayfayı açmasına bağlı olamaz.
        var (tenant, _) = await SeedTenantAsync("Sekme Kapandı");
        var link = await CreateLinkAsync(tenant.ApiKey,
            new { description = "Tek kullanımlık bilet", amountMinor = 120_00, maxUsage = 1 });

        await PayThroughCheckoutAsync(link.Slug);
        // ← sonuc sayfası BİLEREK açılmıyor

        using (var scope = _apiFactory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Poyra.SharedKernel.Tenancy.TenantContext>()
                .SetPlatform();
            await scope.ServiceProvider
                .GetRequiredService<Poyra.Modules.PaymentLinks.Infrastructure.PaymentLinkOutcomeJob>()
                .ResolveAsync();
        }

        var links = await ApiOk<List<LinkDto>>(HttpMethod.Get, "/v1/payment-links", null,
            ("X-Api-Key", tenant.ApiKey));
        links.Single(l => l.Id == link.Id).SuccessCount.ShouldBe(1);

        // Ve bağlantı ikinci kez ÖDENEMEZ
        var blocked = await _checkout.PostAsync($"/l/{link.Slug}/ode", new FormUrlEncodedContent([]));
        blocked.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        blocked.Headers.Location!.ToString().ShouldContain("hata=");
    }

    [Fact]
    public async Task Basarisiz_odeme_baglantiyi_TUKETMEMELI()
    {
        // Deneme kaydı ödeme yaratılırken yazılır; sonucu sunucu yazar. Denemeyi
        // "kullanım" saymak, kartı reddedilen müşterinin bağlantısını yakardı.
        var (tenant, _) = await SeedTenantAsync("Başarısız Deneme");
        var link = await CreateLinkAsync(tenant.ApiKey,
            new { description = "Bilet", amountMinor = 90_00, maxUsage = 1 });

        // mockbank: 3DS formu üretilir ama callback GÖNDERİLMEZ — ödeme askıda kalır
        var response = await _checkout.PostAsync($"/l/{link.Slug}/ode", new FormUrlEncodedContent([]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using (var scope = _apiFactory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Poyra.SharedKernel.Tenancy.TenantContext>()
                .SetPlatform();
            await scope.ServiceProvider
                .GetRequiredService<Poyra.Modules.PaymentLinks.Infrastructure.PaymentLinkOutcomeJob>()
                .ResolveAsync();
        }

        var links = await ApiOk<List<LinkDto>>(HttpMethod.Get, "/v1/payment-links", null,
            ("X-Api-Key", tenant.ApiKey));
        links.Single(l => l.Id == link.Id).SuccessCount.ShouldBe(0);

        // Bağlantı hâlâ ödenebilir
        (await _checkout.GetStringAsync($"/l/{link.Slug}")).ShouldContain("Güvenli ödemeye geç");
    }

    [Fact]
    public async Task Suresi_dolmus_ve_kapatilmis_link_odeme_kabul_etmemeli()
    {
        var (tenant, _) = await SeedTenantAsync("Süre Testi");

        var expired = await CreateLinkAsync(tenant.ApiKey, new
        {
            description = "Süresi dolmuş",
            amountMinor = 50_00,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        (await _checkout.GetStringAsync($"/l/{expired.Slug}")).ShouldContain("süresi dolmuş");

        var disabled = await CreateLinkAsync(tenant.ApiKey,
            new { description = "Kapatılacak", amountMinor = 50_00 });
        var after = await ApiOk<LinkDto>(HttpMethod.Post, $"/v1/payment-links/{disabled.Id}/disable",
            null, ("X-Api-Key", tenant.ApiKey));
        after.Status.ShouldBe("disabled");

        (await _checkout.GetStringAsync($"/l/{disabled.Slug}")).ShouldContain("kapatılmış");

        // Kapalı link silinmez — listede durmaya devam eder (İlke 3)
        var links = await ApiOk<List<LinkDto>>(HttpMethod.Get, "/v1/payment-links", null,
            ("X-Api-Key", tenant.ApiKey));
        links.ShouldContain(l => l.Id == disabled.Id);
    }

    [Fact]
    public async Task Bilinmeyen_slug_404_donmeli()
    {
        var page = await _checkout.GetAsync("/l/olmayan-slug-123");
        page.StatusCode.ShouldBe(HttpStatusCode.OK); // sayfa açılır…
        (await page.Content.ReadAsStringAsync()).ShouldContain("Bağlantı bulunamadı"); // …ama boş

        var pay = await _checkout.PostAsync("/l/olmayan-slug-123/ode", new FormUrlEncodedContent([]));
        pay.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Baglantilar_isyerleri_arasi_sizmamali()
    {
        var (tenantA, _) = await SeedTenantAsync("İşyeri A");
        var (tenantB, _) = await SeedTenantAsync("İşyeri B");

        var linkA = await CreateLinkAsync(tenantA.ApiKey,
            new { description = "A'nın bağlantısı", amountMinor = 10_00 });

        // B listede A'nınkini görmez
        var listB = await ApiOk<List<LinkDto>>(HttpMethod.Get, "/v1/payment-links", null,
            ("X-Api-Key", tenantB.ApiKey));
        listB.ShouldNotContain(l => l.Id == linkA.Id);

        // B, A'nın bağlantısını kapatamaz (RLS + global filtre → bulunamadı)
        var disable = await _api.SendAsync(new HttpRequestMessage(
            HttpMethod.Post, $"/v1/payment-links/{linkA.Id}/disable")
        { Headers = { { "X-Api-Key", tenantB.ApiKey } } });
        disable.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Checkout sayfası ise A'nın işyeri bağlamında açılır — ödeme A'ya yazılır
        var paymentId = await PayThroughCheckoutAsync(linkA.Slug);
        (await _api.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/payments/{paymentId}")
        { Headers = { { "X-Api-Key", tenantB.ApiKey } } })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _api.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/payments/{paymentId}")
        { Headers = { { "X-Api-Key", tenantA.ApiKey } } })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Panel_baglanti_olusturmali_listelemeli_ve_kapatmali()
    {
        var (tenant, email) = await SeedTenantAsync("Panel Bağlantı A.Ş.");
        var panel = await LoginPanelAsync(email, tenant.Slug);

        var create = await panel.PostAsync("/baglantilar-odeme/olustur",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["description"] = "Panelden açılan bağlantı",
                ["amountLira"] = "199,90",
                ["maxInstallments"] = "6",
                ["maxUsage"] = "0",
            }));
        create.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        create.Headers.Location!.ToString().ShouldContain("sonuc=");

        var list = await panel.GetStringAsync("/baglantilar-odeme");
        list.ShouldContain("Panelden açılan bağlantı");
        list.ShouldContain("199,90 ₺");
        list.ShouldContain("6 taksite kadar");
        list.ShouldContain("Açık");

        // API'den de aynı bağlantı görünür (tek gerçek kaynağı)
        var links = await ApiOk<List<LinkDto>>(HttpMethod.Get, "/v1/payment-links", null,
            ("X-Api-Key", tenant.ApiKey));
        var link = links.Single(l => l.Description == "Panelden açılan bağlantı");
        link.AmountMinor.ShouldBe(199_90);
        link.MaxInstallments.ShouldBe(6);

        var close = await panel.PostAsync($"/baglantilar-odeme/{link.Id}/kapat", new FormUrlEncodedContent([]));
        close.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await panel.GetStringAsync("/baglantilar-odeme")).ShouldContain("Kapalı");
    }
}
