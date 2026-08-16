using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Poyra.Checkout;
using Poyra.Panel;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F5.5: white-label checkout (işyeri markası), ERP muhasebe fişi ve QR kod.
/// Üçü de "işyeri Poyra'yı gizleyebilsin ve verisini kendi sistemlerine taşıyabilsin"
/// başlığı altındadır.
/// </summary>
[Collection("postgres")]
public sealed class WhiteLabelAndExportTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "beyaz-parola-123";

    // 1×1 saydam PNG (base64) — gerçek bir görüntü, tür doğrulaması kandırılmıyor
    private const string TinyPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory;
    private readonly WebApplicationFactory<CheckoutEntryPoint> _checkoutFactory;
    private readonly WebApplicationFactory<PanelEntryPoint> _panelFactory;
    private readonly HttpClient _api;
    private readonly HttpClient _checkout;

    public WhiteLabelAndExportTests(PostgresFixture fixture)
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
        _checkoutFactory = new WebApplicationFactory<CheckoutEntryPoint>().WithWebHostBuilder(Configure);
        _panelFactory = new WebApplicationFactory<PanelEntryPoint>().WithWebHostBuilder(Configure);
        _api = _apiFactory.CreateClient();
        _checkout = _checkoutFactory.CreateClient();
    }

    public void Dispose()
    {
        _apiFactory.Dispose();
        _checkoutFactory.Dispose();
        _panelFactory.Dispose();
    }

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record AccountDto(Guid Id);
    private sealed record LinkDto(string Id, string Slug, string Url);
    private sealed record BrandingDto(
        string? DisplayName, string PrimaryColor, bool HasLogo,
        string? SupportEmail, string? SupportPhone, string? CheckoutDomain);
    private sealed record ErpSettingsDto(
        string Format, string PosReceivableAccount, string BankAccount,
        string CommissionExpenseAccount, string DocumentPrefix);
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);
    private sealed record StatementSummary(Guid Id, string Status);

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

    private async Task<(TenantCreated Tenant, string Email)> SeedTenantAsync(string name)
    {
        var email = $"beyaz-{Guid.NewGuid():N}@ornek.com";
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name,
            slug = "beyaz-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "Marka Kullanıcısı",
        }, ("X-Platform-Key", AdminKey));

        await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        return (tenant, email);
    }

    private async Task<HttpClient> LoginPanelAsync(string email, string tenantSlug)
    {
        var panel = _panelFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = Password, ["tenantSlug"] = tenantSlug,
        }))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return panel;
    }

    // ---- White-label -----------------------------------------------------------

    [Fact]
    public async Task Checkout_isyerinin_markasini_gostermeli()
    {
        var (tenant, _) = await SeedTenantAsync("Beyaz Etiket A.Ş.");

        await SendOk<BrandingDto>(HttpMethod.Post, "/v1/branding", new
        {
            displayName = "Şahin Mobilya",
            primaryColor = "#1E7A46",
            supportEmail = "destek@sahinmobilya.com",
            supportPhone = "0212 555 44 33",
            logoBase64 = TinyPng,
            logoContentType = "image/png",
        }, ("X-Api-Key", tenant.ApiKey));

        var link = await SendOk<LinkDto>(HttpMethod.Post, "/v1/payment-links",
            new { description = "Koltuk takımı", amountMinor = 1_499_00 }, ("X-Api-Key", tenant.ApiKey));

        var page = await _checkout.GetStringAsync($"/l/{link.Slug}");

        // Müşteri POYRA'yı değil SATICIYI görmeli — tanımadığı marka ödemeyi bıraktırır
        page.ShouldContain("Şahin Mobilya");
        page.ShouldContain("#1E7A46");                      // marka rengi CSS değişkenine basıldı
        page.ShouldContain($"/l/{link.Slug}/logo");         // logo bu bağlantıya bağlı adresten
        page.ShouldContain("destek@sahinmobilya.com");      // destek görünür (chargeback azaltır)
        page.ShouldContain("0212 555 44 33");

        // Logo gerçekten servis edilir
        var logo = await _checkout.GetAsync($"/l/{link.Slug}/logo");
        logo.StatusCode.ShouldBe(HttpStatusCode.OK);
        logo.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await logo.Content.ReadAsByteArrayAsync()).Length.ShouldBe(Convert.FromBase64String(TinyPng).Length);
    }

    [Fact]
    public async Task Marka_tanimsizsa_isyeri_adina_dusmeli()
    {
        var (tenant, _) = await SeedTenantAsync("Varsayılan Ticaret Ltd.");
        var link = await SendOk<LinkDto>(HttpMethod.Post, "/v1/payment-links",
            new { description = "Hizmet bedeli", amountMinor = 10_000 }, ("X-Api-Key", tenant.ApiKey));

        var page = await _checkout.GetStringAsync($"/l/{link.Slug}");

        page.ShouldContain("Varsayılan Ticaret Ltd.");
        page.ShouldContain("#C4713B"); // Poyra bakırı — geçerli renk yoksa bozuk CSS üretilmez

        // Logo yoksa 404 döner, kırık görsel gösterilmez
        (await _checkout.GetAsync($"/l/{link.Slug}/logo")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Marka_isyerleri_arasi_sizmamali()
    {
        var (tenantA, _) = await SeedTenantAsync("A Ticaret");
        var (tenantB, _) = await SeedTenantAsync("B Ticaret");

        await SendOk<BrandingDto>(HttpMethod.Post, "/v1/branding",
            new { displayName = "A Markası", logoBase64 = TinyPng, logoContentType = "image/png" },
            ("X-Api-Key", tenantA.ApiKey));

        // B kendi markasını okur, A'nınkini değil
        var brandingB = await SendOk<BrandingDto>(HttpMethod.Get, "/v1/branding", null,
            ("X-Api-Key", tenantB.ApiKey));
        brandingB.DisplayName.ShouldBeNull();
        brandingB.HasLogo.ShouldBeFalse();

        var linkB = await SendOk<LinkDto>(HttpMethod.Post, "/v1/payment-links",
            new { description = "B ürünü", amountMinor = 5_000 }, ("X-Api-Key", tenantB.ApiKey));
        (await _checkout.GetStringAsync($"/l/{linkB.Slug}")).ShouldNotContain("A Markası");
    }

    [Fact]
    public async Task Ayni_checkout_alan_adi_iki_isyerine_verilememeli()
    {
        var (tenantA, _) = await SeedTenantAsync("Alan A");
        var (tenantB, _) = await SeedTenantAsync("Alan B");

        await SendOk<BrandingDto>(HttpMethod.Post, "/v1/branding",
            new { checkoutDomain = "odeme.ornekmagaza.com" }, ("X-Api-Key", tenantA.ApiKey));

        // Alan adı işyerini ÇÖZER: çakışma, müşteriyi yanlış markayla karşılamak olurdu
        var conflict = await Send(HttpMethod.Post, "/v1/branding",
            new { checkoutDomain = "ODEME.ornekmagaza.com" }, ("X-Api-Key", tenantB.ApiKey));
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict, await conflict.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Gecersiz_marka_girdisi_reddedilmeli()
    {
        var (tenant, _) = await SeedTenantAsync("Doğrulama A.Ş.");

        var badColor = await Send(HttpMethod.Post, "/v1/branding",
            new { primaryColor = "yeşil" }, ("X-Api-Key", tenant.ApiKey));
        badColor.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var badLogo = await Send(HttpMethod.Post, "/v1/branding",
            new { logoBase64 = TinyPng, logoContentType = "application/pdf" }, ("X-Api-Key", tenant.ApiKey));
        badLogo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var badDomain = await Send(HttpMethod.Post, "/v1/branding",
            new { checkoutDomain = "http://odeme.magaza.com/yol" }, ("X-Api-Key", tenant.ApiKey));
        badDomain.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---- QR --------------------------------------------------------------------

    [Fact]
    public async Task Baglantinin_qr_kodu_svg_donmeli()
    {
        var (tenant, email) = await SeedTenantAsync("QR Ticaret");
        var link = await SendOk<LinkDto>(HttpMethod.Post, "/v1/payment-links",
            new { description = "Masa 4 hesabı", amountMinor = 32_500 }, ("X-Api-Key", tenant.ApiKey));

        var response = await Send(HttpMethod.Get, $"/v1/payment-links/{link.Id}/qr.svg", null,
            ("X-Api-Key", tenant.ApiKey));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");

        var svg = await response.Content.ReadAsStringAsync();
        svg.ShouldStartWith("<svg");
        svg.ShouldContain("viewBox");
        // SVG seçildi: basılınca ölçekten bağımsız keskin kalır, bulanık QR okunmaz
        svg.ShouldContain("crispEdges");

        // Panelden de indirilebilir (işyeri API anahtarı taşımaz)
        var panel = await LoginPanelAsync(email, tenant.Slug);
        var fromPanel = await panel.GetAsync($"/baglantilar-odeme/{link.Id}/qr.svg");
        fromPanel.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fromPanel.Content.ReadAsStringAsync()).ShouldBe(svg);

        (await panel.GetStringAsync("/baglantilar-odeme")).ShouldContain("QR kodu");
    }

    [Fact]
    public async Task Baska_isyerinin_qr_kodu_alinamamali()
    {
        var (tenantA, _) = await SeedTenantAsync("QR A");
        var (tenantB, _) = await SeedTenantAsync("QR B");
        var linkA = await SendOk<LinkDto>(HttpMethod.Post, "/v1/payment-links",
            new { description = "A ürünü", amountMinor = 1_000 }, ("X-Api-Key", tenantA.ApiKey));

        (await Send(HttpMethod.Get, $"/v1/payment-links/{linkA.Id}/qr.svg", null,
            ("X-Api-Key", tenantB.ApiKey))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- ERP -------------------------------------------------------------------

    private async Task<(TenantCreated Tenant, string Email, Guid StatementId)> SeedMatchedStatementAsync()
    {
        var (tenant, email) = await SeedTenantAsync("Muhasebe A.Ş.");
        var accounts = await SendOk<List<Dictionary<string, object>>>(
            HttpMethod.Get, "/v1/connector-accounts", null, ("X-Api-Key", tenant.ApiKey));
        var accountId = accounts[0]["id"].ToString();

        await SendOk<object>(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = accountId, installmentCount = 1, rateBps = 200, valorDays = 1 },
            ("X-Api-Key", tenant.ApiKey));

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        var orderId = payment.NextAction!.Fields["mb_order"];
        await _api.PostAsync(payment.NextAction.Url, new FormUrlEncodedContent(payment.NextAction.Fields));

        var statement = await SendOk<StatementSummary>(HttpMethod.Post, "/v1/recon/statements", new
        {
            connectorAccountId = accountId,
            statementDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)),
            lines = new object[]
            {
                new { orderId, grossMinor = 100_000, commissionMinor = 2_000, netMinor = 98_000 },
            },
        }, ("X-Api-Key", tenant.ApiKey));

        statement.Status.ShouldBe("matched");
        return (tenant, email, statement.Id);
    }

    [Fact]
    public async Task Erp_fisi_dengeli_ve_TR_yaziminda_olmali()
    {
        var (tenant, _, statementId) = await SeedMatchedStatementAsync();

        var response = await Send(HttpMethod.Get, $"/v1/recon/statements/{statementId}/erp-export", null,
            ("X-Api-Key", tenant.ApiKey));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var bytes = await response.Content.ReadAsByteArrayAsync();
        // Excel BOM'suz dosyada Türkçe karakterleri bozar — muhasebeci "ş" yerine "Å" görür
        bytes[..3].ShouldBe([0xEF, 0xBB, 0xBF]);

        var csv = Encoding.UTF8.GetString(bytes[3..]);
        csv.ShouldStartWith("fis_no;tarih");
        csv.ShouldContain("102.01");   // banka
        csv.ShouldContain("653.01");   // komisyon gideri
        csv.ShouldContain("108.01");   // POS alacakları
        csv.ShouldContain("980,00");   // net — TR yazımı
        csv.ShouldContain("20,00");    // komisyon
        csv.ShouldContain("1000,00");  // brüt

        // Çift taraflı kayıt: borç toplamı = alacak toplamı
        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1);
        var (debit, credit) = rows.Aggregate((0m, 0m), (acc, row) =>
        {
            var columns = row.Split(';');
            var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            return (acc.Item1 + decimal.Parse(columns[5], tr), acc.Item2 + decimal.Parse(columns[6], tr));
        });
        debit.ShouldBe(credit);
    }

    [Fact]
    public async Task Erp_hesap_kodlari_ve_bicimi_ayarlanabilmeli()
    {
        var (tenant, _, statementId) = await SeedMatchedStatementAsync();

        await SendOk<ErpSettingsDto>(HttpMethod.Post, "/v1/recon/erp-settings", new
        {
            format = "mikro_csv",
            posReceivableAccount = "108.02.001",
            bankAccount = "102.03",
            commissionExpenseAccount = "760.01",
            documentPrefix = "SANALPOS",
        }, ("X-Api-Key", tenant.ApiKey));

        var response = await Send(HttpMethod.Get, $"/v1/recon/statements/{statementId}/erp-export", null,
            ("X-Api-Key", tenant.ApiKey));
        var csv = Encoding.UTF8.GetString((await response.Content.ReadAsByteArrayAsync())[3..]);

        csv.ShouldStartWith("EvrakNo;EvrakTarihi"); // ayarlanan biçim
        csv.ShouldContain("108.02.001");
        csv.ShouldContain("760.01");
        csv.ShouldContain("SANALPOS-");

        // Adres satırındaki biçim ayarı ezer (aynı gün iki ERP'ye çıkarma ihtiyacı)
        var logo = await Send(HttpMethod.Get,
            $"/v1/recon/statements/{statementId}/erp-export?format=logo_csv", null,
            ("X-Api-Key", tenant.ApiKey));
        Encoding.UTF8.GetString((await logo.Content.ReadAsByteArrayAsync())[3..])
            .ShouldStartWith("FISNO;TARIH");
    }

    [Fact]
    public async Task Gecersiz_hesap_kodu_ve_fis_oneki_reddedilmeli()
    {
        var (tenant, _) = await SeedTenantAsync("Doğrulama Muhasebe");

        var badAccount = await Send(HttpMethod.Post, "/v1/recon/erp-settings",
            new { posReceivableAccount = "kasa" }, ("X-Api-Key", tenant.ApiKey));
        badAccount.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Ayraç içeren önek CSV sütunlarını kaydırırdı
        var badPrefix = await Send(HttpMethod.Post, "/v1/recon/erp-settings",
            new { documentPrefix = "A;B" }, ("X-Api-Key", tenant.ApiKey));
        badPrefix.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var badFormat = await Send(HttpMethod.Post, "/v1/recon/erp-settings",
            new { format = "sap_idoc" }, ("X-Api-Key", tenant.ApiKey));
        badFormat.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Dengesiz_ekstre_icin_fis_uretilmemeli()
    {
        var (tenant, _) = await SeedTenantAsync("Dengesiz Ekstre");
        var accounts = await SendOk<List<Dictionary<string, object>>>(
            HttpMethod.Get, "/v1/connector-accounts", null, ("X-Api-Key", tenant.ApiKey));
        var accountId = accounts[0]["id"].ToString();

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        var orderId = payment.NextAction!.Fields["mb_order"];
        await _api.PostAsync(payment.NextAction.Url, new FormUrlEncodedContent(payment.NextAction.Fields));

        // Bankanın ekstresinde brüt ≠ net + komisyon (1000 ≠ 900 + 20) — gerçekte olur
        var statement = await SendOk<StatementSummary>(HttpMethod.Post, "/v1/recon/statements", new
        {
            connectorAccountId = accountId,
            statementDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)),
            lines = new object[]
            {
                new { orderId, grossMinor = 100_000, commissionMinor = 2_000, netMinor = 90_000 },
            },
        }, ("X-Api-Key", tenant.ApiKey));

        var response = await Send(HttpMethod.Get, $"/v1/recon/statements/{statement.Id}/erp-export", null,
            ("X-Api-Key", tenant.ApiKey));

        // Dengesiz fişi ERP zaten reddeder; sessizce bozuk dosya vermek yerine BURADA söylenir
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("voucher_unbalanced");
        body.ShouldContain("brüt = net + komisyon");
    }

    [Fact]
    public async Task Panelden_marka_ve_erp_ayarlanabilmeli()
    {
        var (tenant, email) = await SeedTenantAsync("Panel Ayar A.Ş.");
        var panel = await LoginPanelAsync(email, tenant.Slug);

        (await panel.GetStringAsync("/ayarlar")).ShouldContain("Ödeme sayfası markası");

        var brand = await panel.PostAsync("/ayarlar/marka",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["displayName"] = "Panelden Marka",
                ["primaryColor"] = "#2244AA",
                ["supportEmail"] = "yardim@panelden.com",
            }));
        brand.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        brand.Headers.Location!.ToString().ShouldContain("sonuc=");

        var erp = await panel.PostAsync("/ayarlar/erp",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["format"] = "logo_csv",
                ["posReceivableAccount"] = "108.05",
                ["bankAccount"] = "102.05",
                ["commissionExpenseAccount"] = "653.05",
                ["documentPrefix"] = "PNL",
            }));
        erp.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var saved = await SendOk<BrandingDto>(HttpMethod.Get, "/v1/branding", null, ("X-Api-Key", tenant.ApiKey));
        saved.DisplayName.ShouldBe("Panelden Marka");
        saved.PrimaryColor.ShouldBe("#2244AA");

        var erpSaved = await SendOk<ErpSettingsDto>(HttpMethod.Get, "/v1/recon/erp-settings", null,
            ("X-Api-Key", tenant.ApiKey));
        erpSaved.Format.ShouldBe("logo_csv");
        erpSaved.DocumentPrefix.ShouldBe("PNL");

        var pageResponse = await panel.GetAsync("/ayarlar");
        var page = await pageResponse.Content.ReadAsStringAsync();
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK, page);
        page.ShouldContain("Panelden Marka");
        page.ShouldContain("108.05");
    }
}
