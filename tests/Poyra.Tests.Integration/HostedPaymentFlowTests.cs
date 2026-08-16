using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F1 uçtan uca: işyeri → mockbank hesabı → ödeme confirm (rota) → "banka" formu callback'e
/// POST → sonuç. Onay, red, failover, void ve kısmi iade senaryoları gerçek HTTP + gerçek
/// RLS'li Postgres üzerinde koşar.
/// </summary>
[Collection("postgres")]
public sealed class HostedPaymentFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _client;

    public HostedPaymentFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _factory = new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Platform:AdminKey", AdminKey);
            builder.UseSetting("Poyra:PublicBaseUrl", "http://localhost");
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
        });
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    // ---- Test DTO'ları (API şekli değişirse burada kırılır — bilinçli) -------
    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record AccountDto(Guid Id, string ConnectorKey, string Label, int Priority, string Status, string Health);
    private sealed record NextAction(string Type, string Url, string Method, Dictionary<string, string> Fields);
    private sealed record ErrorDto(string Code, string? RawCode, string? Message);
    private sealed record PaymentDto(
        string Id, string Status, long AmountMinor, string Currency, string ClientSecret,
        int Installments, string? ReturnUrl, NextAction? NextAction, ErrorDto? LastError);
    private sealed record CallbackResp(string PaymentId, string Status);
    private sealed record RefundDto(string Id, string PaymentId, long AmountMinor, string Status, ErrorDto? Error);

    // ---- Yardımcılar ---------------------------------------------------------
    private async Task<TenantCreated> CreateTenantAsync()
    {
        var response = await Send(HttpMethod.Post, "/v1/tenants",
            new { name = "Akış Testi", slug = "akis-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));
        return (await response.Content.ReadFromJsonAsync<TenantCreated>())!;
    }

    private async Task<AccountDto> AddMockAccountAsync(
        string apiKey, string label, int priority, bool failInitiate = false)
    {
        var credentials = new Dictionary<string, string> { ["secret"] = "s3cret" };
        if (failInitiate)
            credentials["fail_initiate"] = "true";

        var response = await Send(HttpMethod.Post, "/v1/connector-accounts",
            new { connectorKey = "mockbank", label, credentials, priority },
            ("X-Api-Key", apiKey));
        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private async Task<PaymentDto> CreateAndConfirmAsync(string apiKey, long amountMinor)
    {
        var response = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor, currency = "TRY", confirm = true }, ("X-Api-Key", apiKey));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PaymentDto>())!;
    }

    private async Task<CallbackResp> PostBankFormAsync(NextAction action)
    {
        // "Banka" (müşteri tarayıcısı) formu callback'e post eder
        var response = await _client.PostAsync(action.Url, new FormUrlEncodedContent(action.Fields));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CallbackResp>())!;
    }

    private Task<HttpResponseMessage> Send(
        HttpMethod method, string path, object? body, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        return _client.SendAsync(request);
    }

    // ---- Senaryolar ----------------------------------------------------------

    [Fact]
    public async Task Basarili_odeme_confirm_banka_donusu_ve_void()
    {
        var tenant = await CreateTenantAsync();
        await AddMockAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);

        // confirm → rota → RequiresAction + banka formu
        var payment = await CreateAndConfirmAsync(tenant.ApiKey, 14_900);
        payment.Status.ShouldBe("requires_action");
        payment.NextAction.ShouldNotBeNull();
        payment.NextAction.Type.ShouldBe("redirect_form");
        payment.NextAction.Fields.ShouldContainKey("mb_sig");

        // banka dönüşü → succeeded
        var outcome = await PostBankFormAsync(payment.NextAction);
        outcome.Status.ShouldBe("succeeded");
        outcome.PaymentId.ShouldBe(payment.Id);

        // çift POST (banka tekrarı) idempotent olmalı
        (await PostBankFormAsync(payment.NextAction)).Status.ShouldBe("succeeded");

        // GET → succeeded; rota gerekçesi olay defterine ve intent'e yazıldı mı?
        var fetched = await (await Send(HttpMethod.Get, $"/v1/payments/{payment.Id}", null,
            ("X-Api-Key", tenant.ApiKey))).Content.ReadFromJsonAsync<PaymentDto>();
        fetched!.Status.ShouldBe("succeeded");

        await using (var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId)))
        {
            var intent = await db.PaymentIntents.AsNoTracking().SingleAsync(p => p.PublicId == payment.Id);
            intent.RoutingResultJson.ShouldNotBeNull();
            intent.RoutingResultJson.ShouldContain("Öncelik sırası");
        }

        // void → cancelled
        var cancel = await Send(HttpMethod.Post, $"/v1/payments/{payment.Id}/cancel", null,
            ("X-Api-Key", tenant.ApiKey));
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());
        (await cancel.Content.ReadFromJsonAsync<PaymentDto>())!.Status.ShouldBe("cancelled");
    }

    [Fact]
    public async Task Reddedilen_kart_birlesik_hata_koduyla_dusmeli()
    {
        var tenant = await CreateTenantAsync();
        await AddMockAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);

        var payment = await CreateAndConfirmAsync(tenant.ApiKey, 10_099); // %100 == 99 → red
        var outcome = await PostBankFormAsync(payment.NextAction!);
        outcome.Status.ShouldBe("failed");

        var fetched = await (await Send(HttpMethod.Get, $"/v1/payments/{payment.Id}", null,
            ("X-Api-Key", tenant.ApiKey))).Content.ReadFromJsonAsync<PaymentDto>();
        fetched!.Status.ShouldBe("failed");
        fetched.LastError.ShouldNotBeNull();
        fetched.LastError.Code.ShouldBe("poyra.card_declined");
        fetched.LastError.RawCode.ShouldBe("05");
    }

    [Fact]
    public async Task Initiate_dusen_hesapta_failover_ikinci_hesaba_gecmeli()
    {
        var tenant = await CreateTenantAsync();
        var broken = await AddMockAccountAsync(tenant.ApiKey, "Bozuk POS", priority: 1, failInitiate: true);
        var healthy = await AddMockAccountAsync(tenant.ApiKey, "Sağlam POS", priority: 2);

        var payment = await CreateAndConfirmAsync(tenant.ApiKey, 25_000);
        payment.Status.ShouldBe("requires_action"); // ikinci hesap devraldı
        payment.NextAction.ShouldNotBeNull();

        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        var intent = await db.PaymentIntents.AsNoTracking().SingleAsync(p => p.PublicId == payment.Id);
        var attempts = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.PaymentIntentId == intent.Id).OrderBy(a => a.AttemptNo).ToListAsync();

        attempts.Count.ShouldBe(2);
        attempts[0].ConnectorAccountId.ShouldBe(broken.Id);
        attempts[0].ErrorUnifiedCode.ShouldBe("poyra.connector_unavailable");
        attempts[1].ConnectorAccountId.ShouldBe(healthy.Id);

        // failover olayı deftere düştü mü? (silinemez kanıt — İlke 3)
        (await db.PaymentEvents.AsNoTracking()
                .CountAsync(e => e.PaymentIntentId == intent.Id && e.EventType == "attempt.failed"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Kismi_iade_ve_asim_kontrolu()
    {
        var tenant = await CreateTenantAsync();
        await AddMockAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);

        var payment = await CreateAndConfirmAsync(tenant.ApiKey, 50_000);
        await PostBankFormAsync(payment.NextAction!);

        // kısmi iade
        var partial = await Send(HttpMethod.Post, "/v1/refunds",
            new { paymentId = payment.Id, amountMinor = 20_000 }, ("X-Api-Key", tenant.ApiKey));
        partial.StatusCode.ShouldBe(HttpStatusCode.OK, await partial.Content.ReadAsStringAsync());
        var refund1 = await partial.Content.ReadFromJsonAsync<RefundDto>();
        refund1!.Status.ShouldBe("succeeded");
        refund1.AmountMinor.ShouldBe(20_000);

        // kalan tutarı aşan iade → 400
        var over = await Send(HttpMethod.Post, "/v1/refunds",
            new { paymentId = payment.Id, amountMinor = 40_000 }, ("X-Api-Key", tenant.ApiKey));
        over.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await over.Content.ReadAsStringAsync()).ShouldContain("refund.amount_exceeds_remaining");

        // tutar verilmezse kalanın tamamı
        var rest = await Send(HttpMethod.Post, "/v1/refunds",
            new { paymentId = payment.Id }, ("X-Api-Key", tenant.ApiKey));
        var refund2 = await rest.Content.ReadFromJsonAsync<RefundDto>();
        refund2!.AmountMinor.ShouldBe(30_000);
        refund2.Status.ShouldBe("succeeded");

        // GET refund
        var fetched = await (await Send(HttpMethod.Get, $"/v1/refunds/{refund2.Id}", null,
            ("X-Api-Key", tenant.ApiKey))).Content.ReadFromJsonAsync<RefundDto>();
        fetched!.PaymentId.ShouldBe(payment.Id);
    }

    [Fact]
    public async Task Bagli_hesaplar_isyerleri_arasi_gorunmemeli()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        await AddMockAccountAsync(tenantA.ApiKey, "A'nın POS'u", priority: 1);

        var listB = await (await Send(HttpMethod.Get, "/v1/connector-accounts", null,
            ("X-Api-Key", tenantB.ApiKey))).Content.ReadFromJsonAsync<List<AccountDto>>();
        listB!.ShouldBeEmpty();

        // Katman B tek başına: ham SQL ile bile görünmemeli
        await using var db = _fixture.CreateConnectors(PostgresFixture.TenantCtx(tenantB.TenantId));
        var raw = await db.Database
            .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM connector_accounts""")
            .SingleAsync();
        raw.ShouldBe(0L);
    }

    [Fact]
    public async Task Kural_esleyince_rota_gerekce_yazmali()
    {
        var tenant = await CreateTenantAsync();
        await AddMockAccountAsync(tenant.ApiKey, "Birinci", priority: 1);
        await AddMockAccountAsync(tenant.ApiKey, "Yuksek Tutar POS", priority: 2);

        // kural: 100.000 kuruş üzeri → "Yuksek Tutar POS"
        var ruleDoc = new
        {
            rules = new[]
            {
                new
                {
                    name = "yuksek-tutar",
                    when = new { fact = "amount_minor", op = "gte", value = 100_000 },
                    route = new[] { "Yuksek Tutar POS" },
                    reason = "100.000+ kuruş → yüksek tutar POS'u",
                },
            },
        };
        var createRule = await Send(HttpMethod.Post, "/v1/routing/rules",
            new { name = "ana-kural", document = ruleDoc }, ("X-Api-Key", tenant.ApiKey));
        createRule.StatusCode.ShouldBe(HttpStatusCode.OK, await createRule.Content.ReadAsStringAsync());
        var rule = System.Text.Json.JsonDocument.Parse(await createRule.Content.ReadAsStringAsync());
        var ruleId = rule.RootElement.GetProperty("id").GetGuid();

        (await Send(HttpMethod.Post, $"/v1/routing/rules/{ruleId}/activate", null,
            ("X-Api-Key", tenant.ApiKey))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var payment = await CreateAndConfirmAsync(tenant.ApiKey, 150_000);
        payment.Status.ShouldBe("requires_action");

        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        var intent = await db.PaymentIntents.AsNoTracking().SingleAsync(p => p.PublicId == payment.Id);
        intent.RoutingResultJson.ShouldNotBeNull();
        intent.RoutingResultJson.ShouldContain("yüksek tutar POS"); // açıklanabilir yönlendirme
    }
}
