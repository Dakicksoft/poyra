using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F4.1 rota motoru v2: maliyet (en ucuz), kart programı/banka (on-us), performans sinyali
/// ve kural simülatörü — uçtan uca, gerçek işlemler üzerinde.
/// </summary>
[Collection("postgres")]
public sealed class RoutingV2Tests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _client;

    public RoutingV2Tests(PostgresFixture fixture)
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
            builder.UseSetting("Poyra:JwtKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            builder.UseSetting("Poyra:VaultKey", Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
        });
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string ApiKey);
    private sealed record AccountDto(Guid Id, string Label);
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);
    private sealed record SimulationChange(
        string PaymentId, string FromAccount, string ToAccount,
        long? FromCostMinor, long? ToCostMinor, long? SavingMinor, string Reason);
    private sealed record SimulationResult(
        int SampleSize, int ChangedCount, long CurrentCostMinor, long SimulatedCostMinor,
        long EstimatedSavingMinor, int CostUnknownCount, List<SimulationChange> Changes);

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        return _client.SendAsync(request);
    }

    private async Task<T> SendOk<T>(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var response = await Send(method, path, body, headers);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(TenantCreated Tenant, AccountDto Ucuz, AccountDto Pahali)> SeedAsync()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Rota v2", slug = "rota2-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));

        async Task<AccountDto> Add(string label, int priority)
            => await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
            {
                connectorKey = "mockbank",
                label,
                credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
                priority,
            }, ("X-Api-Key", tenant.ApiKey));

        // Öncelik sırası bilinçli TERS: "Pahalı" önce gelir — strateji devreye girmezse o seçilir
        var pahali = await Add("Pahalı POS", 1);
        var ucuz = await Add("Ucuz POS", 2);

        // Komisyon anlaşmaları: rota bunları maliyet sinyali olarak okur
        await SendOk<object>(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = pahali.Id, installmentCount = 1, rateBps = 320, valorDays = 1 },
            ("X-Api-Key", tenant.ApiKey));
        await SendOk<object>(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = ucuz.Id, installmentCount = 1, rateBps = 180, valorDays = 1 },
            ("X-Api-Key", tenant.ApiKey));

        return (tenant, ucuz, pahali);
    }

    private async Task ActivateRuleAsync(string apiKey, object document)
    {
        var created = await Send(HttpMethod.Post, "/v1/routing/rules",
            new { name = "kural-" + Guid.NewGuid().ToString("N")[..8], document }, ("X-Api-Key", apiKey));
        created.StatusCode.ShouldBe(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
        (await Send(HttpMethod.Post, $"/v1/routing/rules/{id}/activate", null, ("X-Api-Key", apiKey)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<JsonElement> RoutingResultAsync(Guid tenantId, string paymentId)
    {
        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenantId));
        var intent = await db.PaymentIntents.AsNoTracking().SingleAsync(p => p.PublicId == paymentId);
        intent.RoutingResultJson.ShouldNotBeNull();
        return JsonDocument.Parse(intent.RoutingResultJson).RootElement.Clone();
    }

    [Fact]
    public async Task Cheapest_stratejisi_dusuk_komisyonlu_pos_u_secmeli()
    {
        var (tenant, ucuz, _) = await SeedAsync();
        await ActivateRuleAsync(tenant.ApiKey, new { strategy = "cheapest" });

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        payment.Status.ShouldBe("requires_action");

        // Öncelikte "Pahalı" önde olmasına rağmen maliyet stratejisi ucuzu seçmeli
        await using (var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId)))
        {
            var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync();
            attempt.ConnectorAccountId.ShouldBe(ucuz.Id);
            attempt.LatencyMs.ShouldNotBeNull(); // gecikme ölçüldü (performans sinyali)
        }

        var routing = await RoutingResultAsync(tenant.TenantId, payment.Id);
        routing.GetProperty("strategy").GetString().ShouldBe("cheapest");
        routing.GetProperty("reason").GetString().ShouldContain("en düşük komisyon");
        // 100.000 kuruş (1.000 ₺) × %1,80 = 1.800 kuruş = 18,00 ₺
        routing.GetProperty("reason").GetString().ShouldContain("18,00 ₺");

        // Karar sinyalleri kayda geçti: maliyetler görünür (açıklanabilirlik)
        var signals = routing.GetProperty("signals").EnumerateArray().ToList();
        signals.Count.ShouldBe(2);
        signals.ShouldContain(s => s.GetProperty("expected_cost_minor").GetInt64() == 1_800);
        signals.ShouldContain(s => s.GetProperty("expected_cost_minor").GetInt64() == 3_200);
    }

    [Fact]
    public async Task Kart_programina_gore_yonlendirme_bin_ile_calismali()
    {
        var (tenant, ucuz, pahali) = await SeedAsync();

        // Platform BIN kataloğu: 540061 → bonus programı, banka 0062
        await SendOk<object>(HttpMethod.Post, "/v1/bins", new
        {
            bins = new[]
            {
                new
                {
                    bin = "540061", bankCode = "0062", bankName = "Örnek Banka",
                    program = "bonus", brand = "mastercard", cardType = "credit", isCommercial = false,
                },
            },
        }, ("X-Platform-Key", AdminKey));

        // Kural: bonus kartı → "Pahalı POS" (kampanya POS'u senaryosu), aksi hâlde en ucuz
        await ActivateRuleAsync(tenant.ApiKey, new
        {
            strategy = "cheapest",
            rules = new[]
            {
                new
                {
                    name = "bonus-kampanya",
                    when = new { fact = "card.program", op = "eq", value = "bonus" },
                    route = new[] { "Pahalı POS" },
                    reason = "Bonus kampanyası bu POS'ta",
                },
            },
        });

        // BIN gönderilen ödeme: kural eşleşmeli → Pahalı POS
        var created = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 50_000, currency = "TRY" }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{created.Id}/confirm",
            new { bin = "540061" }, ("X-Api-Key", tenant.ApiKey));

        var withBin = await RoutingResultAsync(tenant.TenantId, created.Id);
        withBin.GetProperty("reason").GetString().ShouldContain("Bonus kampanyası");
        withBin.GetProperty("candidates")[0].GetGuid().ShouldBe(pahali.Id);

        // BIN gönderilmeyen ödeme: kart kuralı eşleşmez → strateji (cheapest) devreye girer
        var noBin = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 50_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));

        var without = await RoutingResultAsync(tenant.TenantId, noBin.Id);
        without.GetProperty("reason").GetString().ShouldContain("en düşük komisyon");
        without.GetProperty("candidates")[0].GetGuid().ShouldBe(ucuz.Id);
    }

    [Fact]
    public async Task Simulator_kural_degisiminin_tasarrufunu_hesaplamali()
    {
        var (tenant, ucuz, pahali) = await SeedAsync();
        // Varsayılan öncelik: Pahalı POS önde → gerçek işlemler oraya gitsin
        for (var i = 0; i < 3; i++)
        {
            var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
                new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
            await _client.PostAsync(payment.NextAction!.Url,
                new FormUrlEncodedContent(payment.NextAction.Fields));
        }

        await using (var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId)))
        {
            (await db.PaymentAttempts.AsNoTracking().CountAsync(a => a.ConnectorAccountId == pahali.Id))
                .ShouldBe(3);
        }

        // Aday kural: en ucuza yönlendir → 3 işlem de Ucuz POS'a kayar
        var simulation = await SendOk<SimulationResult>(HttpMethod.Post, "/v1/routing/simulate",
            new { document = new { strategy = "cheapest" }, days = 1, limit = 100 },
            ("X-Api-Key", tenant.ApiKey));

        simulation.SampleSize.ShouldBe(3);
        simulation.ChangedCount.ShouldBe(3);
        simulation.CurrentCostMinor.ShouldBe(9_600); // 3 × 100.000 × %3,20
        simulation.SimulatedCostMinor.ShouldBe(5_400); // 3 × 100.000 × %1,80
        simulation.EstimatedSavingMinor.ShouldBe(4_200); // aylık tasarruf tahmini
        simulation.CostUnknownCount.ShouldBe(0);

        var change = simulation.Changes[0];
        change.FromAccount.ShouldBe("Pahalı POS");
        change.ToAccount.ShouldBe("Ucuz POS");
        change.SavingMinor.ShouldBe(1_400);

        // Simülasyon SALT OKUMADIR: gerçek işlemler değişmedi
        await using var check = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        (await check.PaymentAttempts.AsNoTracking().CountAsync(a => a.ConnectorAccountId == ucuz.Id))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Hacim_bolusumu_kovasi_oncelige_ragmen_uygulanmali()
    {
        var (tenant, ucuz, _) = await SeedAsync();
        // %100 tek kovaya: tohum ne olursa olsun bölüşüm Ucuz POS'u seçmeli
        await ActivateRuleAsync(tenant.ApiKey, new
        {
            volumeSplit = new[] { new { account = "Ucuz POS", percent = 100 } },
        });

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        payment.Status.ShouldBe("requires_action");

        // Öncelikte "Pahalı" önde olmasına rağmen bölüşüm kovası ucuzu seçmeli
        await using (var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId)))
        {
            var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync();
            attempt.ConnectorAccountId.ShouldBe(ucuz.Id);
        }

        var routing = await RoutingResultAsync(tenant.TenantId, payment.Id);
        routing.GetProperty("reason").GetString().ShouldContain("Hacim bölüşümü");
    }

    [Fact]
    public async Task Simulator_hacim_bolusumunu_motorla_ayni_uygulamali()
    {
        var (tenant, ucuz, pahali) = await SeedAsync();
        // Varsayılan öncelik: Pahalı POS önde → gerçek işlemler oraya gitsin
        for (var i = 0; i < 2; i++)
        {
            var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
                new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
            await _client.PostAsync(payment.NextAction!.Url,
                new FormUrlEncodedContent(payment.NextAction.Fields));
        }

        // Aday kural: hacmin %100'ü Ucuz POS kovasına → iki işlem de kaymalı.
        // (Eski simülatör bölüşümü yok sayıp 0 değişim raporlardı — motor-simülatör tutarsızlığı.)
        var simulation = await SendOk<SimulationResult>(HttpMethod.Post, "/v1/routing/simulate",
            new
            {
                document = new { volumeSplit = new[] { new { account = "Ucuz POS", percent = 100 } } },
                days = 1,
                limit = 100,
            },
            ("X-Api-Key", tenant.ApiKey));

        simulation.SampleSize.ShouldBe(2);
        simulation.ChangedCount.ShouldBe(2);
        simulation.Changes.ShouldAllBe(c => c.ToAccount == "Ucuz POS");
        simulation.Changes[0].Reason.ShouldContain("Hacim bölüşümü");
        simulation.EstimatedSavingMinor.ShouldBe(2_800); // 2 × 100.000 × (%3,20 − %1,80)
    }

    [Fact]
    public async Task Simulator_bolusum_kovasinda_motorla_ayni_hesaba_dusmeli()
    {
        var (tenant, _, _) = await SeedAsync();
        var document = new
        {
            volumeSplit = new[]
            {
                new { account = "Ucuz POS", percent = 50 },
                new { account = "Pahalı POS", percent = 50 },
            },
        };
        await ActivateRuleAsync(tenant.ApiKey, document);

        // 8 gerçek ödeme: her biri kendi intent tohumuyla bir kovaya düşer
        for (var i = 0; i < 8; i++)
        {
            var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
                new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
            await _client.PostAsync(payment.NextAction!.Url,
                new FormUrlEncodedContent(payment.NextAction.Fields));
        }

        // AYNI doküman simüle edilince hiçbir işlem yer değiştirmemeli: simülatör motorla aynı
        // tohumdan (intent id) aynı kovayı bulur. Tohumlar ayrışsaydı ~yarısı "değişti" çıkardı.
        var simulation = await SendOk<SimulationResult>(HttpMethod.Post, "/v1/routing/simulate",
            new { document, days = 1, limit = 100 }, ("X-Api-Key", tenant.ApiKey));

        simulation.SampleSize.ShouldBe(8);
        simulation.ChangedCount.ShouldBe(0);
    }
}
