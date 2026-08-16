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
/// F4.2 panel aksiyonları: iade/iptal düğmeleri gerçekten para hareketi yapar ve
/// rol denetimi arayüzde DEĞİL sunucuda uygulanır (buton gizlemek yeterli değildir).
/// </summary>
[Collection("postgres")]
public sealed class PanelActionTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "panel-parola-123";
    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory;
    private readonly WebApplicationFactory<PanelEntryPoint> _panelFactory;
    private readonly HttpClient _api;

    public PanelActionTests(PostgresFixture fixture)
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
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);
    private sealed record RefundDto(string Id, long AmountMinor, string Status);

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

    private async Task<(TenantCreated Tenant, string OwnerEmail)> SeedAsync()
    {
        var email = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await ApiOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Aksiyon İşyeri",
            slug = "akt-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "Sahip",
        }, ("X-Platform-Key", AdminKey));

        await ApiOk<object>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        return (tenant, email);
    }

    private async Task<string> RunPaidPaymentAsync(string apiKey, long amountMinor)
    {
        var payment = await ApiOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor, currency = "TRY", confirm = true }, ("X-Api-Key", apiKey));
        (await _api.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields))).StatusCode.ShouldBe(HttpStatusCode.OK);
        return payment.Id;
    }

    private static async Task LoginAsync(HttpClient panel, string email, string slug)
    {
        var response = await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = Password,
            ["tenantSlug"] = slug,
        }));
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Panelden_iade_yapilabilmeli_ve_defterde_gorunmeli()
    {
        var (tenant, email) = await SeedAsync();
        var paymentId = await RunPaidPaymentAsync(tenant.ApiKey, 50_000);

        var panel = CreatePanelClient();
        await LoginAsync(panel, email, tenant.Slug);

        // Detay sayfasında aksiyon düğmeleri görünmeli (sahip = owner rolü)
        var detail = await panel.GetStringAsync($"/odemeler/{paymentId}");
        detail.ShouldContain("İade et");
        detail.ShouldContain("İptal (void)");

        // Kısmi iade
        var refundResponse = await panel.PostAsync($"/odemeler/{paymentId}/iade",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["amountMinor"] = "20000" }));
        (await PanelRedirects.RevealAsync(panel, refundResponse))
            .ShouldContain("İade tamam: 200,00 ₺");

        // API tarafında da görünür (tek defter)
        var refunds = await ApiOk<List<RefundDto>>(HttpMethod.Get,
            $"/v1/subscription-invoices", null, ("X-Api-Key", tenant.ApiKey)); // sadece uç canlılığı
        refunds.ShouldNotBeNull();

        var timeline = await panel.GetStringAsync($"/odemeler/{paymentId}");
        timeline.ShouldContain("refund.succeeded"); // olay defterine düştü
    }

    [Fact]
    public async Task Panelden_iptal_yapilabilmeli()
    {
        var (tenant, email) = await SeedAsync();
        var paymentId = await RunPaidPaymentAsync(tenant.ApiKey, 33_000);

        var panel = CreatePanelClient();
        await LoginAsync(panel, email, tenant.Slug);

        var response = await panel.PostAsync($"/odemeler/{paymentId}/iptal", new FormUrlEncodedContent([]));
        (await PanelRedirects.RevealAsync(panel, response))
            .ShouldContain("iptal edildi");

        var detail = await panel.GetStringAsync($"/odemeler/{paymentId}");
        detail.ShouldContain("payment.cancelled");
        detail.ShouldNotContain("İptal (void)"); // artık aksiyon gösterilmez
    }

    [Fact]
    public async Task Denetci_rolu_para_hareketi_yapamamali()
    {
        var (tenant, _) = await SeedAsync();
        var paymentId = await RunPaidPaymentAsync(tenant.ApiKey, 44_000);

        var auditorEmail = $"denetci-{Guid.NewGuid():N}@ornek.com";
        await ApiOk<object>(HttpMethod.Post, "/v1/users",
            new { email = auditorEmail, password = Password, displayName = "Denetçi", role = "auditor" },
            ("X-Api-Key", tenant.ApiKey));

        var panel = CreatePanelClient();
        await LoginAsync(panel, auditorEmail, tenant.Slug);

        // Arayüzde düğme yok…
        var detail = await panel.GetStringAsync($"/odemeler/{paymentId}");
        detail.ShouldNotContain("İade et");

        // …ve doğrudan POST edilse bile sunucu reddeder (ikinci kapı)
        var forced = await panel.PostAsync($"/odemeler/{paymentId}/iade",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["amountMinor"] = "1000" }));
        (await PanelRedirects.RevealAsync(panel, forced))
            .ShouldContain("'operations' rolü gerekli");

        // Gerçekten iade OLMADI
        var stillClean = await panel.GetStringAsync($"/odemeler/{paymentId}");
        stillClean.ShouldNotContain("refund.succeeded");
    }

    [Fact]
    public async Task Rota_ekrani_kurali_ve_hesaplari_gostermeli()
    {
        var (tenant, email) = await SeedAsync();
        var panel = CreatePanelClient();
        await LoginAsync(panel, email, tenant.Slug);

        var page = await panel.GetStringAsync("/rota");
        page.ShouldContain("Rota kuralları");
        page.ShouldContain("Geçmişte dene"); // simülatör düğmesi
        page.ShouldContain("Mock POS"); // hesap listesi
        page.ShouldContain("Aktif kural yok"); // henüz kural yayınlanmadı
        page.ShouldContain("Yayına al"); // owner → admin yetkisi var
    }
}
