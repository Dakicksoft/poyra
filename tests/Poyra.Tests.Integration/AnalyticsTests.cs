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
/// F6.1 Analitik: "nerede kaybediyorum" ve "gerçek maliyetim ne". Oranlar DENEME bazındadır —
/// ödeme bazında ölçmek failover'ı gizler ve bozuk POS'u iyi gösterir; testler bunu doğrular.
/// </summary>
[Collection("postgres")]
public sealed class AnalyticsTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "analiz-parola-123";

    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory;
    private readonly WebApplicationFactory<PanelEntryPoint> _panelFactory;
    private readonly HttpClient _api;

    public AnalyticsTests(PostgresFixture fixture)
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
    private sealed record AccountDto(Guid Id, string Label);
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);
    private sealed record BreakdownRow(
        string Key, string Label, int Attempts, int Succeeded, double SuccessRate, long GmvMinor,
        Guid? AccountId);
    private sealed record FailureRow(string Code, string Label, int Count, double Share);
    private sealed record HourRow(int Hour, int Attempts, int Succeeded, long GmvMinor);
    private sealed record DayRow(DateOnly Day, int Attempts, int Succeeded, long GmvMinor);
    private sealed record OverviewDto(
        int Days, DateOnly FromDay, DateOnly ToDay, int PaymentCount, int Attempts, int Succeeded,
        double SuccessRate, long GmvMinor, long RefundedMinor, long AverageBasketMinor,
        int? MedianLatencyMs, List<BreakdownRow> ByConnector, List<BreakdownRow> ByInstallment,
        List<BreakdownRow> ByCardProgram, List<FailureRow> Failures, List<HourRow> ByHour,
        List<DayRow> ByDay);
    private sealed record CostRow(
        int Installments, int Count, long GrossMinor, long CommissionMinor,
        int EffectiveRateBps, long DeltaMinor);
    private sealed record CostDto(
        int Days, DateOnly FromDay, DateOnly ToDay, int AuditedCount, int UnauditedCount,
        long GrossMinor, long CommissionMinor, int EffectiveRateBps, long ExpectedCommissionMinor,
        long DeltaMinor, List<CostRow> ByInstallment);

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
        var email = $"analiz-{Guid.NewGuid():N}@ornek.com";
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Analiz A.Ş.",
            slug = "analiz-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "Analiz Kullanıcısı",
        }, ("X-Platform-Key", AdminKey));
        return (tenant, email);
    }

    private async Task<AccountDto> AddAccountAsync(
        string apiKey, string label, int priority, bool broken = false)
    {
        var credentials = new Dictionary<string, string> { ["secret"] = "s3cret" };
        if (broken)
            credentials["fail_initiate"] = "true";

        return await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts",
            new { connectorKey = "mockbank", label, credentials, priority }, ("X-Api-Key", apiKey));
    }

    /// <summary>Ödemeyi başlatır ve banka dönüşünü yaptırır; sonuç tutarın kuruşundan gelir.</summary>
    private async Task<string> RunPaymentAsync(string apiKey, long amountMinor, int installments = 1)
    {
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor, currency = "TRY", installments, confirm = true }, ("X-Api-Key", apiKey));

        if (payment.NextAction is { } action)
            await _api.PostAsync(action.Url, new FormUrlEncodedContent(action.Fields));

        return payment.Id;
    }

    // ---- Senaryolar ----------------------------------------------------------

    [Fact]
    public async Task Ozet_basari_oranini_deneme_bazinda_olcmeli()
    {
        var (tenant, _) = await SeedTenantAsync();
        await AddAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);

        await RunPaymentAsync(tenant.ApiKey, 10_000); // onay
        await RunPaymentAsync(tenant.ApiKey, 20_000); // onay
        await RunPaymentAsync(tenant.ApiKey, 10_099); // kuruş 99 → kart reddi

        var overview = await SendOk<OverviewDto>(HttpMethod.Get, "/v1/analytics/overview?days=7", null,
            ("X-Api-Key", tenant.ApiKey));

        overview.Days.ShouldBe(7);
        overview.PaymentCount.ShouldBe(3);
        overview.Attempts.ShouldBe(3);
        overview.Succeeded.ShouldBe(2);
        overview.SuccessRate.ShouldBe(2d / 3, 0.001);
        overview.GmvMinor.ShouldBe(30_000);
        overview.AverageBasketMinor.ShouldBe(15_000);

        // Başarısızlık nedeni işyerinin anlayacağı dilde
        var failure = overview.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe("poyra.card_declined");
        failure.Label.ShouldBe("Banka onaylamadı");
        failure.Share.ShouldBe(1d, 0.001);
    }

    [Fact]
    public async Task Failover_denemesi_oranı_dusurmeli_odeme_bazinda_gizlenmemeli()
    {
        var (tenant, _) = await SeedTenantAsync();
        await AddAccountAsync(tenant.ApiKey, "Bozuk POS", priority: 1, broken: true);
        await AddAccountAsync(tenant.ApiKey, "Yedek POS", priority: 2);

        await RunPaymentAsync(tenant.ApiKey, 50_000); // bozuk POS düşer, yedek tahsil eder

        var overview = await SendOk<OverviewDto>(HttpMethod.Get, "/v1/analytics/overview?days=7", null,
            ("X-Api-Key", tenant.ApiKey));

        // Ödeme başarılı ama BANKAYA İKİ İSTEK gitti — oran bunu göstermeli
        overview.PaymentCount.ShouldBe(1);
        overview.Attempts.ShouldBe(2);
        overview.Succeeded.ShouldBe(1);
        overview.SuccessRate.ShouldBe(0.5, 0.001);

        // POS kırılımı bozuk hesabı ayrı gösterir — hangi POS'un sorunlu olduğu görünür
        overview.ByConnector.Sum(c => c.Attempts).ShouldBe(2);
        overview.Failures.ShouldContain(f => f.Code == "poyra.connector_unavailable");
    }

    [Fact]
    public async Task Taksit_ve_kart_programi_kirilimlari_dolmali()
    {
        var (tenant, _) = await SeedTenantAsync();
        var account = await AddAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);

        await SendOk<object>(HttpMethod.Post, "/v1/installments/schemes",
            new { connectorAccountId = account.Id, program = "bonus", installmentCount = 3, customerRateBps = 500 },
            ("X-Api-Key", tenant.ApiKey));
        await SendOk<object>(HttpMethod.Post, "/v1/bins", new
        {
            bins = new[]
            {
                new
                {
                    bin = "540061", bankCode = "0062", bankName = "Garanti BBVA",
                    program = "bonus", brand = "mastercard", cardType = "credit", isCommercial = false,
                },
            },
        }, ("X-Platform-Key", AdminKey));

        // Tek çekim (program bilinmiyor — BIN gönderilmedi)
        await RunPaymentAsync(tenant.ApiKey, 10_000);

        // 3 taksit, BIN ile → kart programı denemeye yazılmalı
        var created = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 60_000, currency = "TRY", installments = 3 }, ("X-Api-Key", tenant.ApiKey));
        var confirmed = await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{created.Id}/confirm",
            new { bin = "540061" }, ("X-Api-Key", tenant.ApiKey));
        await _api.PostAsync(confirmed.NextAction!.Url, new FormUrlEncodedContent(confirmed.NextAction.Fields));

        var overview = await SendOk<OverviewDto>(HttpMethod.Get, "/v1/analytics/overview?days=7", null,
            ("X-Api-Key", tenant.ApiKey));

        overview.ByInstallment.ShouldContain(r => r.Key == "1" && r.Label == "Tek çekim");
        overview.ByInstallment.ShouldContain(r => r.Key == "3" && r.Label == "3 taksit");

        // Kart programı BIN'den türetilip denemeye iliştirildi — kampanya ölçülebilir
        var bonus = overview.ByCardProgram.SingleOrDefault(r => r.Key == "bonus");
        bonus.ShouldNotBeNull("kart programı denemeye yazılmalı");
        bonus.Succeeded.ShouldBe(1);
        bonus.GmvMinor.ShouldBe(63_000); // %5 vade farkıyla çekilen tutar

        // BIN gönderilmeyen işlem "(bilinmiyor)" altında — sessizce bir programa yazılmaz
        overview.ByCardProgram.ShouldContain(r => r.Key == "(bilinmiyor)");
    }

    [Fact]
    public async Task Gun_ve_saat_serileri_bosluk_birakmamali()
    {
        var (tenant, _) = await SeedTenantAsync();
        await AddAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);
        await RunPaymentAsync(tenant.ApiKey, 10_000);

        var overview = await SendOk<OverviewDto>(HttpMethod.Get, "/v1/analytics/overview?days=7", null,
            ("X-Api-Key", tenant.ApiKey));

        // Boş günler ATLANMAZ: grafikte delik bırakmak trendi yanlış gösterir
        overview.ByDay.Count.ShouldBe(7);
        overview.ByDay.First().Day.ShouldBe(overview.FromDay);
        overview.ByDay.Last().Day.ShouldBe(overview.ToDay);
        overview.ByDay.Sum(d => d.Attempts).ShouldBe(1);

        overview.ByHour.Count.ShouldBe(24);
        overview.ByHour.Select(h => h.Hour).ShouldBe(Enumerable.Range(0, 24));

        // Gün anahtarı TÜRKİYE günü (UTC+3)
        var trToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        overview.ToDay.ShouldBe(trToday);
    }

    [Fact]
    public async Task Iade_gmv_den_dusulmemeli_ayri_gosterilmeli()
    {
        var (tenant, _) = await SeedTenantAsync();
        await AddAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);
        var paymentId = await RunPaymentAsync(tenant.ApiKey, 40_000);

        await SendOk<object>(HttpMethod.Post, "/v1/refunds",
            new { paymentId, amountMinor = 15_000 }, ("X-Api-Key", tenant.ApiKey));

        var overview = await SendOk<OverviewDto>(HttpMethod.Get, "/v1/analytics/overview?days=7", null,
            ("X-Api-Key", tenant.ApiKey));

        // GMV tahsil edilendir; iade ayrı satırdır — netleştirmek "ne sattım" sorusunu bozar
        overview.GmvMinor.ShouldBe(40_000);
        overview.RefundedMinor.ShouldBe(15_000);
    }

    [Fact]
    public async Task Maliyet_ozeti_gercek_kesintiyi_ve_farki_vermeli()
    {
        var (tenant, _) = await SeedTenantAsync();
        var account = await AddAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);

        await SendOk<object>(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = account.Id, installmentCount = 1, rateBps = 200, valorDays = 1 },
            ("X-Api-Key", tenant.ApiKey));

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        var orderId = payment.NextAction!.Fields["mb_order"];
        await _api.PostAsync(payment.NextAction.Url, new FormUrlEncodedContent(payment.NextAction.Fields));

        // Anlaşma %2 → beklenen 2.000; banka 2.500 kesmiş → +500 fazla kesim
        await SendOk<object>(HttpMethod.Post, "/v1/recon/statements", new
        {
            connectorAccountId = account.Id,
            statementDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)),
            lines = new object[]
            {
                new { orderId, grossMinor = 100_000, commissionMinor = 2_500, netMinor = 97_500 },
            },
        }, ("X-Api-Key", tenant.ApiKey));

        var cost = await SendOk<CostDto>(HttpMethod.Get, "/v1/recon/cost-summary?days=7", null,
            ("X-Api-Key", tenant.ApiKey));

        cost.AuditedCount.ShouldBe(1);
        cost.UnauditedCount.ShouldBe(0);
        cost.GrossMinor.ShouldBe(100_000);
        cost.CommissionMinor.ShouldBe(2_500);         // BANKANIN kestiği
        cost.ExpectedCommissionMinor.ShouldBe(2_000); // anlaşmanın söylediği
        cost.DeltaMinor.ShouldBe(500);                // itiraz konusu
        cost.EffectiveRateBps.ShouldBe(250);          // gerçekleşen %2,50

        var row = cost.ByInstallment.ShouldHaveSingleItem();
        row.Installments.ShouldBe(1);
        row.EffectiveRateBps.ShouldBe(250);
    }

    [Fact]
    public async Task Anlasmasiz_satir_fark_yok_ile_karistirilmamali()
    {
        var (tenant, _) = await SeedTenantAsync();
        var account = await AddAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);

        // Anlaşma TANIMLANMADI
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 50_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        var orderId = payment.NextAction!.Fields["mb_order"];
        await _api.PostAsync(payment.NextAction.Url, new FormUrlEncodedContent(payment.NextAction.Fields));

        await SendOk<object>(HttpMethod.Post, "/v1/recon/statements", new
        {
            connectorAccountId = account.Id,
            statementDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)),
            lines = new object[]
            {
                new { orderId, grossMinor = 50_000, commissionMinor = 1_000, netMinor = 49_000 },
            },
        }, ("X-Api-Key", tenant.ApiKey));

        var cost = await SendOk<CostDto>(HttpMethod.Get, "/v1/recon/cost-summary?days=7", null,
            ("X-Api-Key", tenant.ApiKey));

        // "Ölçemedik" ile "fark yok" aynı şey değildir — karıştırmak olmayan bir güven verir
        cost.AuditedCount.ShouldBe(0);
        cost.UnauditedCount.ShouldBe(1);
        cost.DeltaMinor.ShouldBe(0);
        cost.CommissionMinor.ShouldBe(1_000); // gerçek maliyet yine de görünür
    }

    [Fact]
    public async Task Analiz_isyerleri_arasi_sizmamali()
    {
        var (tenantA, _) = await SeedTenantAsync();
        var (tenantB, _) = await SeedTenantAsync();
        await AddAccountAsync(tenantA.ApiKey, "A POS", priority: 1);
        await RunPaymentAsync(tenantA.ApiKey, 77_000);

        var overviewB = await SendOk<OverviewDto>(HttpMethod.Get, "/v1/analytics/overview?days=7", null,
            ("X-Api-Key", tenantB.ApiKey));

        overviewB.Attempts.ShouldBe(0);
        overviewB.GmvMinor.ShouldBe(0);
        overviewB.ByConnector.ShouldBeEmpty();
        // Boş işyerinde da seriler dolu döner — arayüz boş diziye karşı savunma yapmak zorunda kalmaz
        overviewB.ByHour.Count.ShouldBe(24);
        overviewB.ByDay.Count.ShouldBe(7);
    }

    [Fact]
    public async Task Panel_analiz_ekrani_gercek_veriyi_gostermeli()
    {
        var (tenant, email) = await SeedTenantAsync();
        await AddAccountAsync(tenant.ApiKey, "Panel POS", priority: 1);
        await RunPaymentAsync(tenant.ApiKey, 125_000);
        await RunPaymentAsync(tenant.ApiKey, 10_099); // reddedilen

        var panel = _panelFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = Password, ["tenantSlug"] = tenant.Slug,
        }))).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var page = await panel.GetStringAsync("/analiz?gun=7");

        page.ShouldContain("1.250,00");           // GMV, TR biçimi
        page.ShouldContain("Başarı oranı");
        page.ShouldContain("%50");                // 2 denemede 1 başarı
        page.ShouldContain("Banka onaylamadı");   // başarısızlık nedeni Türkçe
        page.ShouldContain("Panel POS");          // POS kırılımı
        // Küçük örnekte oran gösterilmez — 2 denemede "%50 başarı" yanlış karar verdirir
        page.ShouldContain("yetersiz örnek");
    }
}
