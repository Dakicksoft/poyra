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
        long EstimatedSavingMinor, int CostUnknownCount, int UnroutableCount, int ForcedCount,
        List<SimulationChange> Changes);

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

    [Fact]
    public async Task Simulator_karar_aninda_bilinmeyen_karti_sonradan_ogrenmemeli()
    {
        var (tenant, _, _) = await SeedAsync();

        // 1. ödeme: bin İPUCUSUZ confirm — motor kararı kartsız verir; kart (540667…)
        // ancak banka callback'inde öğrenilir ve MaskedPan'a yazılır
        var bilinmeyen = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        (await _client.PostAsync(bilinmeyen.NextAction!.Url,
            new FormUrlEncodedContent(bilinmeyen.NextAction.Fields))).IsSuccessStatusCode.ShouldBeTrue();

        // 2. ödeme: AYNI bin confirm'de verilir — motor kararı kartla verir
        var bilinen = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY" }, ("X-Api-Key", tenant.ApiKey));
        var confirmed = await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{bilinen.Id}/confirm",
            new { bin = "540667" }, ("X-Api-Key", tenant.ApiKey));
        (await _client.PostAsync(confirmed.NextAction!.Url,
            new FormUrlEncodedContent(confirmed.NextAction.Fields))).IsSuccessStatusCode.ShouldBeTrue();

        // Testin öncülü: 1. ödemenin MaskedPan'ı callback'te gerçekten kaydedildi ve kural
        // önekiyle (5406) eşleşiyor — eski simülatör tam bu yüzden onu da kaydırırdı
        await using (var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId)))
        {
            var intent = await db.PaymentIntents.AsNoTracking()
                .SingleAsync(p => p.PublicId == bilinmeyen.Id);
            var attempt = await db.PaymentAttempts.AsNoTracking()
                .SingleAsync(a => a.PaymentIntentId == intent.Id);
            attempt.MaskedPan.ShouldNotBeNull();
            attempt.MaskedPan.ShouldStartWith("540667");
        }

        // Aday kural: 5406… kartları Ucuz POS'a. Karar anında kartı BİLİNEN ödeme kayar;
        // bilinmeyen ödeme kaymaz — MaskedPan'dan sonradan öğrenilen kart kurallara sızmamalı.
        var simulation = await SendOk<SimulationResult>(HttpMethod.Post, "/v1/routing/simulate",
            new
            {
                document = new
                {
                    rules = new[]
                    {
                        new
                        {
                            name = "5406-kampanya",
                            when = new { fact = "bin", op = "starts_with", value = "5406" },
                            route = new[] { "Ucuz POS" },
                        },
                    },
                },
                days = 1,
                limit = 100,
            },
            ("X-Api-Key", tenant.ApiKey));

        simulation.SampleSize.ShouldBe(2);
        simulation.ChangedCount.ShouldBe(1); // eski simülatör her ikisini de kaydırırdı
        simulation.Changes.Single().PaymentId.ShouldBe(bilinen.Id);
        simulation.Changes.Single().ToAccount.ShouldBe("Ucuz POS");
    }

    [Fact]
    public async Task Simulator_taksit_semasi_olmayan_pos_a_kaydirmamali()
    {
        var (tenant, ucuz, pahali) = await SeedAsync();

        // 3 taksit komisyon anlaşmaları: maliyet sinyalinde Ucuz yine ucuz
        await SendOk<object>(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = pahali.Id, installmentCount = 3, rateBps = 320, valorDays = 1 },
            ("X-Api-Key", tenant.ApiKey));
        await SendOk<object>(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = ucuz.Id, installmentCount = 3, rateBps = 180, valorDays = 1 },
            ("X-Api-Key", tenant.ApiKey));

        // Taksit şeması YALNIZ Pahalı'da — Ucuz 3 taksidi gerçekte işleyemez
        await SendOk<object>(HttpMethod.Post, "/v1/installments/schemes",
            new { connectorAccountId = pahali.Id, program = "*", installmentCount = 3, customerRateBps = 0 },
            ("X-Api-Key", tenant.ApiKey));

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY", installments = 3, confirm = true },
            ("X-Api-Key", tenant.ApiKey));
        await _client.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields));

        // "En ucuz" aday kuralın zincir başı Ucuz'dur ama şeması yok: confirm döngüsü gibi
        // simülatör de Pahalı'ya düşmeli — POS değişimi ve sahte tasarruf raporlanmamalı
        var simulation = await SendOk<SimulationResult>(HttpMethod.Post, "/v1/routing/simulate",
            new { document = new { strategy = "cheapest" }, days = 1, limit = 100 },
            ("X-Api-Key", tenant.ApiKey));

        simulation.SampleSize.ShouldBe(1);
        simulation.ChangedCount.ShouldBe(0); // eski simülatör "Ucuz'a kayar, 1.400 kuruş tasarruf" derdi
        simulation.UnroutableCount.ShouldBe(0);
        simulation.EstimatedSavingMinor.ShouldBe(0);

        // maxAttempts=1: confirm yalnız zincir başını denerdi — işlem yönlendirilemez sayılmalı.
        // DİKKAT: bu blok üstteki bloğun da kanıtıdır — UnroutableCount=1 ancak zincir başı
        // gerçekten Ucuz (ve şemasız) ise çıkar; silinirse üstteki assert'ler dişsiz kalır.
        var darZincir = await SendOk<SimulationResult>(HttpMethod.Post, "/v1/routing/simulate",
            new
            {
                document = new { strategy = "cheapest", guards = new { maxAttempts = 1 } },
                days = 1,
                limit = 100,
            },
            ("X-Api-Key", tenant.ApiKey));

        darZincir.UnroutableCount.ShouldBe(1);
        darZincir.ChangedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Simulator_eski_kayitta_maskedpan_yaklasiklamasina_dusmeli()
    {
        var (tenant, _, _) = await SeedAsync();

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        (await _client.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields))).IsSuccessStatusCode.ShouldBeTrue();

        object binRule = new
        {
            document = new
            {
                rules = new[]
                {
                    new
                    {
                        name = "5406-kampanya",
                        when = new { fact = "bin", op = "starts_with", value = "5406" },
                        route = new[] { "Ucuz POS" },
                    },
                },
            },
            days = 1,
            limit = 100,
        };

        // Kaydı ESKİ biçime çevir: "card" anahtarı yok → simülatör MaskedPan yaklaşıklamasına
        // düşmeli ve kural (540667… önekiyle) eşleşmeli. Deploy sonrası pencere dolana kadar
        // üretim trafiğinin çoğu bu daldan geçecek.
        await RewriteRoutingResultAsync(tenant.TenantId, payment.Id, "{}");
        var eski = await SendOk<SimulationResult>(
            HttpMethod.Post, "/v1/routing/simulate", binRule, ("X-Api-Key", tenant.ApiKey));
        eski.ChangedCount.ShouldBe(1);
        eski.Changes.Single().ToAccount.ShouldBe("Ucuz POS");

        // Nesne olmayan kök ("[]" jsonb'de geçerlidir): 500 yerine yine fallback çalışmalı
        await RewriteRoutingResultAsync(tenant.TenantId, payment.Id, "[]");
        var bozuk = await SendOk<SimulationResult>(
            HttpMethod.Post, "/v1/routing/simulate", binRule, ("X-Api-Key", tenant.ApiKey));
        bozuk.ChangedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Simulator_elle_sabitlenen_odemeyi_kural_kaydirmis_gibi_gostermemeli()
    {
        var (tenant, _, pahali) = await SeedAsync();

        // İşyeri hesabı ELLE seçiyor — kural devrede değil ve kural değişse de zorlama sürer
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY" }, ("X-Api-Key", tenant.ApiKey));
        var confirmed = await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{payment.Id}/confirm",
            new { connectorAccountId = pahali.Id }, ("X-Api-Key", tenant.ApiKey));
        (await _client.PostAsync(confirmed.NextAction!.Url,
            new FormUrlEncodedContent(confirmed.NextAction.Fields))).IsSuccessStatusCode.ShouldBeTrue();

        // "En ucuz" aday kural bu ödemeyi Ucuz'a kaydırırmış gibi tasarruf raporlamamalı
        var simulation = await SendOk<SimulationResult>(HttpMethod.Post, "/v1/routing/simulate",
            new { document = new { strategy = "cheapest" }, days = 1, limit = 100 },
            ("X-Api-Key", tenant.ApiKey));

        simulation.SampleSize.ShouldBe(1);
        simulation.ForcedCount.ShouldBe(1);
        simulation.ChangedCount.ShouldBe(0);
        simulation.EstimatedSavingMinor.ShouldBe(0);
    }

    private async Task RewriteRoutingResultAsync(Guid tenantId, string paymentId, string json)
    {
        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenantId));
        var intent = await db.PaymentIntents.SingleAsync(p => p.PublicId == paymentId);
        intent.RoutingResultJson = json;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Kanal_kurali_api_odemesini_hedef_pos_a_yonlendirmeli()
    {
        var (tenant, _, pahali) = await SeedAsync();

        // Kural: API'den gelen ödemeler öncelikli hatta, gerisi en ucuza.
        // Strateji "cheapest" olduğu için kural olmasaydı Ucuz POS seçilirdi.
        await ActivateRuleAsync(tenant.ApiKey, new
        {
            strategy = "cheapest",
            rules = new[]
            {
                new
                {
                    name = "api-hatti",
                    when = new { fact = "channel", op = "eq", value = "api" },
                    route = new[] { "Pahalı POS" },
                    reason = "API kanalı → öncelikli hat",
                },
            },
        });

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));

        await using (var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId)))
        {
            var intent = await db.PaymentIntents.AsNoTracking().SingleAsync(p => p.PublicId == payment.Id);
            intent.Channel.ShouldBe("api");

            var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync();
            attempt.ConnectorAccountId.ShouldBe(pahali.Id); // kanal kuralı maliyeti ezdi
        }

        (await RoutingResultAsync(tenant.TenantId, payment.Id))
            .GetProperty("reason").GetString().ShouldContain("API kanalı");
    }

    [Fact]
    public async Task Isyeri_kanali_istek_govdesiyle_degistirememeli()
    {
        // Kanal, rota kuralının dayandığı bir sinyaldir; işyerinin beyanına bırakılsaydı
        // "saha tahsilatı" kuralına API'den istek atarak girilebilirdi. Uç nokta kanalı
        // kendisi yazar, gövdedeki alan bağlanmaz.
        var (tenant, ucuz, _) = await SeedAsync();
        await ActivateRuleAsync(tenant.ApiKey, new
        {
            strategy = "cheapest",
            rules = new[]
            {
                new
                {
                    name = "saha-hatti",
                    when = new { fact = "channel", op = "eq", value = "field" },
                    route = new[] { "Pahalı POS" },
                    reason = "saha → Pahalı POS",
                },
            },
        });

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY", confirm = true, channel = "field" },
            ("X-Api-Key", tenant.ApiKey));

        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        var intent = await db.PaymentIntents.AsNoTracking().SingleAsync(p => p.PublicId == payment.Id);
        intent.Channel.ShouldBe("api"); // gövdedeki "field" yok sayıldı

        var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync();
        attempt.ConnectorAccountId.ShouldBe(ucuz.Id); // saha kuralı eşleşmedi → strateji
    }


    [Fact]
    public async Task On_us_orani_cheapest_stratejisinin_kazananini_degistirmeli()
    {
        var (tenant, ucuz, pahali) = await SeedAsync();

        // "Pahalı POS" aslında Garanti'nin POS'u: kendi kartına %1,20 (on-us), gerisine %3,20.
        // Genel oranlarda Ucuz POS (%1,80) kazanır; Garanti kartında on-us öne geçmeli.
        await SendOk<object>(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = pahali.Id, installmentCount = 1, rateBps = 120, valorDays = 1,
                  bankCode = "0062" },
            ("X-Api-Key", tenant.ApiKey));

        await SendOk<object>(HttpMethod.Post, "/v1/bins", new
        {
            bins = new[]
            {
                new
                {
                    bin = "540061", bankCode = "0062", bankName = "Garanti", program = "bonus",
                    brand = "mastercard", cardType = "credit", isCommercial = false,
                },
                new
                {
                    bin = "450803", bankCode = "0064", bankName = "İş Bankası", program = "maximum",
                    brand = "visa", cardType = "credit", isCommercial = false,
                },
            },
        }, ("X-Platform-Key", AdminKey));

        await ActivateRuleAsync(tenant.ApiKey, new { strategy = "cheapest" });

        // Garanti kartı: on-us %1,20 → Pahalı POS
        var onUs = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY" }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{onUs.Id}/confirm",
            new { bin = "540061" }, ("X-Api-Key", tenant.ApiKey));

        // İş Bankası kartı: on-us yok → genel oranlar → Ucuz POS
        var offUs = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY" }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{offUs.Id}/confirm",
            new { bin = "450803" }, ("X-Api-Key", tenant.ApiKey));

        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        var attempts = await db.PaymentAttempts.AsNoTracking()
            .Join(db.PaymentIntents.AsNoTracking(), a => a.PaymentIntentId, i => i.Id,
                (a, i) => new { i.PublicId, a.ConnectorAccountId })
            .ToListAsync();

        attempts.Single(a => a.PublicId == onUs.Id).ConnectorAccountId.ShouldBe(pahali.Id);
        attempts.Single(a => a.PublicId == offUs.Id).ConnectorAccountId.ShouldBe(ucuz.Id);

        // Gerekçe on-us oranını yazmalı: 100.000 kuruş × %1,20 = 1.200 kuruş = 12,00 ₺
        (await RoutingResultAsync(tenant.TenantId, onUs.Id))
            .GetProperty("reason").GetString().ShouldContain("12,00 ₺");
    }

    [Fact]
    public async Task Kart_bilinmiyorsa_on_us_orani_uygulanmamali()
    {
        // Hosted akışta müşteri henüz kart girmemiş olabilir (bin gönderilmez). On-us
        // varsayıp ucuz oran seçmek, rotayı gerçekte daha PAHALI olan POS'a yollardı.
        var (tenant, ucuz, pahali) = await SeedAsync();

        await SendOk<object>(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = pahali.Id, installmentCount = 1, rateBps = 120, valorDays = 1,
                  bankCode = "0062" },
            ("X-Api-Key", tenant.ApiKey));

        await ActivateRuleAsync(tenant.ApiKey, new { strategy = "cheapest" });

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));

        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        (await db.PaymentAttempts.AsNoTracking().SingleAsync())
            .ConnectorAccountId.ShouldBe(ucuz.Id); // genel oranlar: %1,80 < %3,20
    }

    [Fact]
    public async Task Yurt_disi_kart_kurali_ulke_koduyla_eslesmeli()
    {
        var (tenant, ucuz, pahali) = await SeedAsync();

        await SendOk<object>(HttpMethod.Post, "/v1/bins", new
        {
            bins = new[]
            {
                new
                {
                    bin = "411111", bankCode = "XXXX", bankName = "Yabancı Banka", program = "other",
                    brand = "visa", cardType = "credit", isCommercial = false, country = "DE",
                },
            },
        }, ("X-Platform-Key", AdminKey));

        // Yurt dışı kart "Pahalı POS"a (e-ihracat hattı senaryosu); gerisi en ucuza
        await ActivateRuleAsync(tenant.ApiKey, new
        {
            strategy = "cheapest",
            rules = new[]
            {
                new
                {
                    name = "yurt-disi",
                    when = new { fact = "card.country", op = "ne", value = "TR" },
                    route = new[] { "Pahalı POS" },
                    reason = "yurt dışı kart → e-ihracat hattı",
                },
            },
        });

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 100_000, currency = "TRY" }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{payment.Id}/confirm",
            new { bin = "411111" }, ("X-Api-Key", tenant.ApiKey));

        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        (await db.PaymentAttempts.AsNoTracking().SingleAsync())
            .ConnectorAccountId.ShouldBe(pahali.Id);

        (await RoutingResultAsync(tenant.TenantId, payment.Id))
            .GetProperty("reason").GetString().ShouldContain("yurt dışı");

        // Karar-anı kartına ülke de yazıldı — simülatör aynı kararı yeniden oynatabilsin
        (await RoutingResultAsync(tenant.TenantId, payment.Id))
            .GetProperty("card").GetProperty("country").GetString().ShouldBe("DE");
    }

}
