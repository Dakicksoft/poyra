using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// Gözden geçirmede bulunan boşluk: sekiz uç HTTP düzeyinde hiç sınanmamıştı.
/// Birkaçının KOMUTU panel testlerinden geçiyordu ama uç katmanı (yol, rol kuralı,
/// istek bağlama) sınanmıyordu — yol yanlış yazılsa ya da rol kuralı kaysa
/// hiçbir test kırmızı yanmazdı.
/// </summary>
[Collection("postgres")]
public sealed class EndpointCoverageTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "kapsam-parola-123";

    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _api;

    public EndpointCoverageTests(PostgresFixture fixture)
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
            builder.UseSetting("Poyra:VaultKey", Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
        });
        _api = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record AccountDto(Guid Id, string Label, string Status);
    private sealed record PlanDto(string Id, string Name, bool Active);
    private sealed record WebhookDto(Guid Id, string Url, bool Active, string? Secret);
    private sealed record CapabilityDto(string Key, string Title, bool Available, string Reason, List<string> Accounts);
    private sealed record CapabilitiesDto(int ActiveAccounts, int HealthyAccounts, List<CapabilityDto> Capabilities);
    private sealed record TenantMeDto(
        Guid TenantId, Guid OrganizationId, string Name, string Slug, string Status,
        DateTimeOffset CreatedAt);
    private sealed record AckDto(bool Accepted);
    private sealed record RoutingRuleDto(Guid Id, string Name, int Version, bool IsActive);

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

    private async Task<(TenantCreated Tenant, string OwnerEmail)> SeedAsync(bool withConnector = true)
    {
        var email = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Kapsam A.Ş.",
            slug = "kps-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "Sahip",
        }, ("X-Platform-Key", AdminKey));

        if (withConnector)
        {
            await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
            {
                connectorKey = "mockbank",
                label = "Mock POS",
                credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
                priority = 1,
            }, ("X-Api-Key", tenant.ApiKey));
        }

        return (tenant, email);
    }

    // ---- /v1/platform/capabilities ---------------------------------------------------

    [Fact]
    public async Task Yetenek_matrisi_POS_kurulumunu_yansitmali()
    {
        var (empty, _) = await SeedAsync(withConnector: false);

        var none = await SendOk<CapabilitiesDto>(
            HttpMethod.Get, "/v1/platform/capabilities", null, ("X-Api-Key", empty.ApiKey));

        none.ActiveAccounts.ShouldBe(0);
        none.Capabilities.ShouldAllBe(c => !c.Available);
        none.Capabilities.ShouldContain(c => c.Key == "payments.installments");

        var (withPos, _) = await SeedAsync();
        var ready = await SendOk<CapabilitiesDto>(
            HttpMethod.Get, "/v1/platform/capabilities", null, ("X-Api-Key", withPos.ApiKey));

        ready.ActiveAccounts.ShouldBe(1);
        ready.Capabilities.Single(c => c.Key == "payments.refund").Available.ShouldBeTrue();
        ready.Capabilities.Single(c => c.Key == "payments.refund").Accounts.ShouldContain("Mock POS");
    }

    [Fact]
    public async Task Yurt_disi_saglayici_taksit_yetenegi_vermemeli()
    {
        var (tenant, _) = await SeedAsync(withConnector: false);
        await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "stripe",
            label = "Stripe",
            credentials = new Dictionary<string, string> { ["secret_key"] = "sk_test_x" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        var capabilities = await SendOk<CapabilitiesDto>(
            HttpMethod.Get, "/v1/platform/capabilities", null, ("X-Api-Key", tenant.ApiKey));

        // Yurt dışı sağlayıcıda TR banka taksidi yoktur — matris bunu göstermeli
        var installments = capabilities.Capabilities.Single(c => c.Key == "payments.installments");
        installments.Available.ShouldBeFalse();
        installments.Reason.ShouldContain("taksit");
    }

    // ---- /v1/tenants/me ----------------------------------------------------------------

    [Fact]
    public async Task Isyeri_kendini_okuyabilmeli()
    {
        var (tenant, _) = await SeedAsync(withConnector: false);

        var me = await SendOk<TenantMeDto>(HttpMethod.Get, "/v1/tenants/me", null,
            ("X-Api-Key", tenant.ApiKey));

        me.TenantId.ShouldBe(tenant.TenantId);
        me.Slug.ShouldBe(tenant.Slug);

        // Anahtarsız erişilememeli
        (await Send(HttpMethod.Get, "/v1/tenants/me", null)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- /v1/routing/rules/active --------------------------------------------------------

    [Fact]
    public async Task Aktif_rota_kurali_okunabilmeli()
    {
        var (tenant, _) = await SeedAsync();

        // Kural yayınlanmadan da uç çalışmalı (varsayılan strateji geçerli)
        var before = await Send(HttpMethod.Get, "/v1/routing/rules/active", null,
            ("X-Api-Key", tenant.ApiKey));
        before.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);

        // Kural oluşturmak onu AKTİF YAPMAZ — aktivasyon bilinçli olarak ayrı bir
        // adımdır (yanlış kuralın kazayla yayına çıkması tahsilatı durdururdu)
        var draft = await SendOk<RoutingRuleDto>(HttpMethod.Post, "/v1/routing/rules", new
        {
            name = "Kapsam kuralı",
            document = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                """{"rules":[],"fallback":["Mock POS"],"strategy":"priority"}"""),
        }, ("X-Api-Key", tenant.ApiKey));

        draft.IsActive.ShouldBeFalse();
        (await Send(HttpMethod.Get, "/v1/routing/rules/active", null, ("X-Api-Key", tenant.ApiKey)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await SendOk<RoutingRuleDto>(HttpMethod.Post, $"/v1/routing/rules/{draft.Id}/activate",
            null, ("X-Api-Key", tenant.ApiKey));

        var after = await Send(HttpMethod.Get, "/v1/routing/rules/active", null,
            ("X-Api-Key", tenant.ApiKey));
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await after.Content.ReadAsStringAsync()).ShouldContain("Mock POS");
    }

    // ---- Durum değiştiren uçlar -----------------------------------------------------------

    [Fact]
    public async Task POS_hesabi_ucundan_kapatilip_acilabilmeli()
    {
        var (tenant, _) = await SeedAsync();
        var accounts = await SendOk<List<AccountDto>>(HttpMethod.Get, "/v1/connector-accounts", null,
            ("X-Api-Key", tenant.ApiKey));
        var account = accounts.ShouldHaveSingleItem();

        var disabled = await SendOk<AccountDto>(HttpMethod.Post,
            $"/v1/connector-accounts/{account.Id}/status",
            new { status = "disabled" }, ("X-Api-Key", tenant.ApiKey));
        disabled.Status.ShouldBe("disabled");

        // Kapalı POS rota dışında kalır → ödeme rota bulamaz
        var payment = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        payment.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var enabled = await SendOk<AccountDto>(HttpMethod.Post,
            $"/v1/connector-accounts/{account.Id}/status",
            new { status = "active" }, ("X-Api-Key", tenant.ApiKey));
        enabled.Status.ShouldBe("active");
    }

    [Fact]
    public async Task Plan_ucundan_kapatilabilmeli()
    {
        var (tenant, _) = await SeedAsync();
        var plan = await SendOk<PlanDto>(HttpMethod.Post, "/v1/plans",
            new { name = "Kapanacak", amountMinor = 10_000 }, ("X-Api-Key", tenant.ApiKey));

        var closed = await SendOk<PlanDto>(HttpMethod.Post, $"/v1/plans/{plan.Id}/status",
            new { active = false }, ("X-Api-Key", tenant.ApiKey));
        closed.Active.ShouldBeFalse();

        var reopened = await SendOk<PlanDto>(HttpMethod.Post, $"/v1/plans/{plan.Id}/status",
            new { active = true }, ("X-Api-Key", tenant.ApiKey));
        reopened.Active.ShouldBeTrue();
    }

    [Fact]
    public async Task Webhook_ucu_ucundan_kapatilip_sirri_dondurulebilmeli()
    {
        var (tenant, _) = await SeedAsync(withConnector: false);
        var endpoint = await SendOk<WebhookDto>(HttpMethod.Post, "/v1/webhook-endpoints",
            new { url = "https://sisteminiz.example/poyra", eventTypes = new[] { "payment.succeeded" } },
            ("X-Api-Key", tenant.ApiKey));
        var firstSecret = endpoint.Secret.ShouldNotBeNull();

        var rotated = await SendOk<WebhookDto>(HttpMethod.Post,
            $"/v1/webhook-endpoints/{endpoint.Id}/rotate-secret", null, ("X-Api-Key", tenant.ApiKey));
        rotated.Secret.ShouldNotBeNull().ShouldNotBe(firstSecret);

        var disabled = await SendOk<WebhookDto>(HttpMethod.Post,
            $"/v1/webhook-endpoints/{endpoint.Id}/status",
            new { active = false }, ("X-Api-Key", tenant.ApiKey));
        disabled.Active.ShouldBeFalse();

        // Listede sır DÖNMEZ — yalnız oluşturma ve döndürmede bir kez görünür
        var list = await Send(HttpMethod.Get, "/v1/webhook-endpoints", null, ("X-Api-Key", tenant.ApiKey));
        (await list.Content.ReadAsStringAsync()).ShouldNotContain("whsec_");
    }

    // ---- /v1/auth/email/send-verification -------------------------------------------------

    [Fact]
    public async Task Dogrulama_postasi_yeniden_istenebilmeli()
    {
        var (tenant, _) = await SeedAsync(withConnector: false);

        var response = await Send(HttpMethod.Post, "/v1/auth/email/send-verification", new { },
            ("X-Api-Key", tenant.ApiKey));

        // Makine çağrısında "hangi kullanıcı" belli değildir — uç kullanıcı oturumu ister
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Unauthorized,
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Dogrulama_postasi_kullanici_oturumuyla_istenebilmeli()
    {
        var (tenant, ownerEmail) = await SeedAsync(withConnector: false);

        var login = await SendOk<Dictionary<string, System.Text.Json.JsonElement>>(
            HttpMethod.Post, "/v1/auth/login",
            new { email = ownerEmail, password = Password, tenantSlug = tenant.Slug });
        var token = login["accessToken"].GetString()!;

        var response = await Send(HttpMethod.Post, "/v1/auth/email/send-verification", new { },
            ("Authorization", $"Bearer {token}"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    // ---- İşyeri zorunluluğu: saha ve defter önekleri --------------------------------------

    // Bu önekler RequiresTenant listesinden düşerse anahtarsız istek 401 yerine işleyiciye
    // sızar ve RLS katmanında 500 olarak patlar. Gövdedeki "tenant_required" reddin işyeri
    // korumasından geldiğini kanıtlar — başka kaynaklı bir 401 testi yeşil bırakamaz.
    [Fact]
    public async Task Saha_ve_defter_uclari_anahtarsiz_reddedilmeli()
    {
        (HttpMethod Method, string Path)[] endpoints =
        [
            (HttpMethod.Post, "/v1/field/sync"),
            (HttpMethod.Post, "/v1/ledger/settings"),
            (HttpMethod.Post, "/v1/settlements/upload"),
            (HttpMethod.Get, "/v1/receivables"),
        ];

        foreach (var (method, path) in endpoints)
        {
            var response = await Send(method, path, null);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{method} {path} → {body}");
            body.ShouldContain("tenant_required", customMessage: $"{method} {path}");
        }
    }
}
