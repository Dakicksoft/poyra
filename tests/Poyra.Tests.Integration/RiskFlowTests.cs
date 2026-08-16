using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Poyra.Api;
using Poyra.Modules.Payments.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// M15 Risk motoru. Asıl soru "kural eşleşiyor mu" değil, <b>para gerçekten
/// durdu mu</b>: engellenen işlem bankaya HİÇ gitmemeli ve deneme kaydı açılmamalıdır.
/// </summary>
[Collection("postgres")]
public sealed class RiskFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string TestVisa = "4355084355084358";

    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _api;

    public RiskFlowTests(PostgresFixture fixture)
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
        _api = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);
    private sealed record RuleSetDto(Guid Id, int Version, string Document, bool Active);
    private sealed record BlocklistDto(Guid Id, string Kind, string Value, string Reason, bool Removed);
    private sealed record AssessmentDto(
        Guid Id, string PaymentId, string Outcome, string? RuleName, string? Reason,
        int? RuleVersion, string Flow, string Signals);
    private sealed record TestResultDto(string Outcome, string? RuleName, string? Reason);

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

    private async Task<TenantCreated> SeedTenantAsync()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Risk A.Ş.", slug = "rsk-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));

        await SendOk<object>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        return tenant;
    }

    private Task<RuleSetDto> PublishAsync(string apiKey, string document)
        => SendOk<RuleSetDto>(HttpMethod.Post, "/v1/risk/rules", new { document }, ("X-Api-Key", apiKey));

    // ---- Motor kurulu değilken -----------------------------------------------------

    [Fact]
    public async Task Kural_yokken_odeme_gecmeli()
    {
        var tenant = await SeedTenantAsync();

        // Risk motorunun yokluğu ya da boş kural seti tahsilatı DURDURMAMALI
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 50_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));

        payment.Status.ShouldBe("requires_action");

        // 'allow' kararı da kaydedilir — "o gün hangi kural bakmıştı" sorusu sonradan gelir
        var assessments = await SendOk<List<AssessmentDto>>(
            HttpMethod.Get, "/v1/risk/assessments", null, ("X-Api-Key", tenant.ApiKey));
        assessments.ShouldHaveSingleItem().Outcome.ShouldBe("allow");
    }

    // ---- block ----------------------------------------------------------------------

    [Fact]
    public async Task Engellenen_odeme_BANKAYA_HIC_GITMEMELI()
    {
        var tenant = await SeedTenantAsync();
        await PublishAsync(tenant.ApiKey, """
            {
              "rules": [
                { "name": "aşırı tutar", "outcome": "block",
                  "when": { "fact": "amount_minor", "op": "gte", "value": 1000000 },
                  "reason": "10.000 ₺ üzeri işlem elle onay ister." }
              ],
              "default": "allow"
            }
            """);

        var response = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 1_500_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("risk.blocked");
        body.ShouldContain("elle onay"); // gerekçe işyerinin kendi metni

        // ★ Asıl kanıt: hiç deneme açılmadı — POS seçilmedi, bankaya gidilmedi
        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        (await db.PaymentAttempts.CountAsync()).ShouldBe(0);
        var intent = await db.PaymentIntents.AsNoTracking().SingleAsync();
        intent.Status.ShouldBe(PaymentStatus.Created); // intent açık kalır, kaybolmaz
        intent.RiskDecisionJson.ShouldNotBeNull();
        intent.RiskDecisionJson.ShouldContain("block");
    }

    [Fact]
    public async Task Esik_altindaki_odeme_gecmeli()
    {
        var tenant = await SeedTenantAsync();
        await PublishAsync(tenant.ApiKey, """
            {"rules":[{"name":"aşırı tutar","outcome":"block",
                       "when":{"fact":"amount_minor","op":"gte","value":1000000}}],
             "default":"allow"}
            """);

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 999_999, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));

        payment.Status.ShouldBe("requires_action");
    }

    // ---- challenge (3DS zorunlu) ------------------------------------------------------

    [Fact]
    public async Task Challenge_direct_akisi_reddetmeli_hosted_akisi_gecirmeli()
    {
        var tenant = await SeedTenantAsync();
        await PublishAsync(tenant.ApiKey, """
            {
              "rules": [
                { "name": "yüksek tutar 3DS ister", "outcome": "challenge",
                  "when": { "fact": "amount_minor", "op": "gte", "value": 100000 },
                  "reason": "1.000 ₺ üzeri işlemde 3D Secure zorunludur." }
              ],
              "default": "allow"
            }
            """);

        // ① direct (3DS'siz) → REDDEDİLİR: kart hamili doğrulaması yok, chargeback
        //    sorumluluğu işyerinde kalır
        var intent = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 250_000, currency = "TRY" }, ("X-Api-Key", tenant.ApiKey));

        var direct = await Send(HttpMethod.Post, $"/v1/payments/{intent.Id}/confirm-direct",
            new { cardNumber = TestVisa, expiryMonth = 12, expiryYear = 2031 },
            ("X-Api-Key", tenant.ApiKey));

        direct.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await direct.Content.ReadAsStringAsync()).ShouldContain("risk.three_ds_required");

        await using (var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId)))
        {
            (await db.PaymentAttempts.CountAsync()).ShouldBe(0); // bankaya gidilmedi
        }

        // ② AYNI ödeme hosted (3DS) akışıyla geçmeli — kural 3DS istiyordu, engellemiyordu
        var hosted = await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{intent.Id}/confirm",
            new { }, ("X-Api-Key", tenant.ApiKey));
        hosted.Status.ShouldBe("requires_action");
    }

    // ---- review ------------------------------------------------------------------------

    [Fact]
    public async Task Review_odemeyi_gecirmeli_ama_isaretlemeli()
    {
        var tenant = await SeedTenantAsync();
        await PublishAsync(tenant.ApiKey, """
            {"rules":[{"name":"gözden geçir","outcome":"review",
                       "when":{"fact":"amount_minor","op":"gte","value":10000},
                       "reason":"Elle bakılsın."}],
             "default":"allow"}
            """);

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 50_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));

        // review DURDURMAZ — durdursaydı "review" ile "block" arasında fark kalmazdı
        payment.Status.ShouldBe("requires_action");

        var flagged = await SendOk<List<AssessmentDto>>(
            HttpMethod.Get, "/v1/risk/assessments?outcome=review", null, ("X-Api-Key", tenant.ApiKey));
        var assessment = flagged.ShouldHaveSingleItem();
        assessment.PaymentId.ShouldBe(payment.Id);
        assessment.RuleName.ShouldBe("gözden geçir");
        assessment.RuleVersion.ShouldBe(1);
        assessment.Flow.ShouldBe("hosted");
    }

    // ---- Kara liste ---------------------------------------------------------------------

    [Fact]
    public async Task Kara_listedeki_musteri_engellenmeli()
    {
        var tenant = await SeedTenantAsync();
        await SendOk<BlocklistDto>(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "customer_ref", value = "DOLANDIRICI-42", reason = "3 chargeback" },
            ("X-Api-Key", tenant.ApiKey));

        // Değer normalleştirilir: büyük/küçük harf farkı engeli kaçırmamalı
        var blocked = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, currency = "TRY", customerRef = "dolandirici-42", confirm = true },
            ("X-Api-Key", tenant.ApiKey));

        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await blocked.Content.ReadAsStringAsync()).ShouldContain("risk.blocked");

        // Başka müşteri etkilenmez
        var clean = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, currency = "TRY", customerRef = "temiz-1", confirm = true },
            ("X-Api-Key", tenant.ApiKey));
        clean.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Kara_liste_kurallardan_ONCE_bakilmali()
    {
        var tenant = await SeedTenantAsync();

        // Kural "her şeye izin ver" dese bile kara liste kazanır: açık bir insan
        // kararını bir kural ezmemelidir
        await PublishAsync(tenant.ApiKey, """{"rules":[{"name":"hepsi geçsin","outcome":"allow"}],"default":"allow"}""");
        await SendOk<BlocklistDto>(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "customer_ref", value = "kara-liste", reason = "sahtecilik" },
            ("X-Api-Key", tenant.ApiKey));

        var response = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 5_000, currency = "TRY", customerRef = "kara-liste", confirm = true },
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var assessments = await SendOk<List<AssessmentDto>>(
            HttpMethod.Get, "/v1/risk/assessments", null, ("X-Api-Key", tenant.ApiKey));
        assessments.ShouldHaveSingleItem().RuleName.ShouldBe("blocklist");
    }

    [Fact]
    public async Task Kara_listeden_cikarilan_gecmeli_ve_kayit_SILINMEMELI()
    {
        var tenant = await SeedTenantAsync();
        var entry = await SendOk<BlocklistDto>(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "customer_ref", value = "yanlislikla", reason = "hata" },
            ("X-Api-Key", tenant.ApiKey));

        await SendOk<object>(HttpMethod.Delete, $"/v1/risk/blocklist/{entry.Id}", null,
            ("X-Api-Key", tenant.ApiKey));

        var afterRemoval = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 5_000, currency = "TRY", customerRef = "yanlislikla", confirm = true },
            ("X-Api-Key", tenant.ApiKey));
        afterRemoval.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Kayıt duruyor: "kim, neden engelledi ve kim açtı" şikâyet incelemesinde sorulur
        var all = await SendOk<List<BlocklistDto>>(
            HttpMethod.Get, "/v1/risk/blocklist?includeRemoved=true", null, ("X-Api-Key", tenant.ApiKey));
        all.ShouldHaveSingleItem().Removed.ShouldBeTrue();

        var active = await SendOk<List<BlocklistDto>>(
            HttpMethod.Get, "/v1/risk/blocklist", null, ("X-Api-Key", tenant.ApiKey));
        active.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ayni_deger_iki_kez_eklenememeli()
    {
        var tenant = await SeedTenantAsync();
        await SendOk<BlocklistDto>(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "ip", value = "203.0.113.7", reason = "kart deneme" }, ("X-Api-Key", tenant.ApiKey));

        var again = await Send(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "ip", value = "203.0.113.7", reason = "yine" }, ("X-Api-Key", tenant.ApiKey));

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).ShouldContain("risk.blocklist_duplicate");
    }

    [Fact]
    public async Task Gerekcesiz_engel_reddedilmeli()
    {
        var tenant = await SeedTenantAsync();

        // Sebepsiz engel, sonradan kimsenin kaldırmaya cesaret edemediği engeldir
        var response = await Send(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "ip", value = "203.0.113.9", reason = "" }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Suresi_gecmis_engel_uygulanmamali()
    {
        var tenant = await SeedTenantAsync();
        await SendOk<BlocklistDto>(HttpMethod.Post, "/v1/risk/blocklist", new
        {
            kind = "customer_ref",
            value = "gecici",
            reason = "24 saatlik soğutma",
            expiresAt = DateTimeOffset.UtcNow.AddHours(-1), // süresi dün doldu
        }, ("X-Api-Key", tenant.ApiKey));

        var response = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 5_000, currency = "TRY", customerRef = "gecici", confirm = true },
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- Hız (velocity) ------------------------------------------------------------------

    [Fact]
    public async Task Hiz_sayaci_kart_deneme_saldirisini_yakalamali()
    {
        var tenant = await SeedTenantAsync();
        await PublishAsync(tenant.ApiKey, """
            {
              "rules": [
                { "name": "kart deneme", "outcome": "block",
                  "when": { "fact": "velocity.attempts_1h", "op": "gte", "value": 3 },
                  "reason": "Kısa sürede çok deneme." }
              ],
              "default": "allow"
            }
            """);

        // Aynı müşteri arka arkaya dener; her confirm bir deneme kaydı açar
        for (var i = 0; i < 3; i++)
        {
            var ok = await Send(HttpMethod.Post, "/v1/payments",
                new { amountMinor = 10_000 + i, currency = "TRY", customerRef = "hizli", confirm = true },
                ("X-Api-Key", tenant.ApiKey));
            ok.StatusCode.ShouldBe(HttpStatusCode.OK, $"{i}. deneme geçmeliydi");
        }

        var blocked = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_004, currency = "TRY", customerRef = "hizli", confirm = true },
            ("X-Api-Key", tenant.ApiKey));

        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await blocked.Content.ReadAsStringAsync()).ShouldContain("risk.blocked");

        // Başka müşteri etkilenmemeli — sayaç müşteri bazlıdır
        var other = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_005, currency = "TRY", customerRef = "yavas", confirm = true },
            ("X-Api-Key", tenant.ApiKey));
        other.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- Kural yönetimi --------------------------------------------------------------------

    [Fact]
    public async Task Yeni_surum_eskisini_pasiflestirmeli_ama_SILMEMELI()
    {
        var tenant = await SeedTenantAsync();
        await PublishAsync(tenant.ApiKey, """{"rules":[],"default":"allow"}""");
        var second = await PublishAsync(tenant.ApiKey, """{"rules":[],"default":"review"}""");

        second.Version.ShouldBe(2);
        second.Active.ShouldBeTrue();

        var versions = await SendOk<List<RuleSetDto>>(
            HttpMethod.Get, "/v1/risk/rules", null, ("X-Api-Key", tenant.ApiKey));

        versions.Count.ShouldBe(2); // eski sürüm duruyor
        versions.Count(v => v.Active).ShouldBe(1); // ama tek aktif
        versions.Single(v => v.Version == 1).Active.ShouldBeFalse();
    }

    [Fact]
    public async Task Gecersiz_sonuc_yayinlanamamali()
    {
        var tenant = await SeedTenantAsync();

        // Motor bilinmeyen sonucu 'review'a düşürür; sessizce yapması işyerinin
        // "block yazdım" sanmasına yol açardı — yayınlamada reddedilir
        var response = await Send(HttpMethod.Post, "/v1/risk/rules",
            new { document = """{"rules":[{"name":"x","outcome":"reddet"}],"default":"allow"}""" },
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Geçersiz sonuç");
    }

    [Fact]
    public async Task Bozuk_dokuman_yayinlanamamali()
    {
        var tenant = await SeedTenantAsync();

        var response = await Send(HttpMethod.Post, "/v1/risk/rules",
            new { document = "{bu json değil" }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Kural_yayinlanmadan_denenebilmeli()
    {
        var tenant = await SeedTenantAsync();
        const string document = """
            {"rules":[{"name":"gece yüksek tutar","outcome":"challenge",
                       "when":{"all":[{"fact":"amount_minor","op":"gte","value":500000},
                                      {"fact":"hour","op":"lte","value":5}]},
                       "reason":"Gece yarısı yüksek tutar."}],
             "default":"allow"}
            """;

        var atNight = await SendOk<TestResultDto>(HttpMethod.Post, "/v1/risk/test",
            new { document, amountMinor = 750_000, hour = 3 }, ("X-Api-Key", tenant.ApiKey));
        atNight.Outcome.ShouldBe("challenge");
        atNight.RuleName.ShouldBe("gece yüksek tutar");

        var atNoon = await SendOk<TestResultDto>(HttpMethod.Post, "/v1/risk/test",
            new { document, amountMinor = 750_000, hour = 14 }, ("X-Api-Key", tenant.ApiKey));
        atNoon.Outcome.ShouldBe("allow");

        // Deneme SALT OKURDUR: kural yayınlanmadı, karar kaydı açılmadı
        (await SendOk<List<RuleSetDto>>(HttpMethod.Get, "/v1/risk/rules", null,
            ("X-Api-Key", tenant.ApiKey))).ShouldBeEmpty();
        (await SendOk<List<AssessmentDto>>(HttpMethod.Get, "/v1/risk/assessments", null,
            ("X-Api-Key", tenant.ApiKey))).ShouldBeEmpty();
    }

    // ---- Yalıtım ve defter --------------------------------------------------------------

    [Fact]
    public async Task Baska_isyerinin_kurali_ve_kara_listesi_gorunmemeli()
    {
        var first = await SeedTenantAsync();
        var second = await SeedTenantAsync();

        await PublishAsync(first.ApiKey, """{"rules":[{"name":"engelle","outcome":"block"}],"default":"block"}""");
        await SendOk<BlocklistDto>(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "customer_ref", value = "ortak", reason = "x" }, ("X-Api-Key", first.ApiKey));

        // ★ İkinci işyerinin ödemesi, birincinin "hepsini engelle" kuralından ETKİLENMEMELİ
        var payment = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 20_000, currency = "TRY", customerRef = "ortak", confirm = true },
            ("X-Api-Key", second.ApiKey));
        payment.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await SendOk<List<RuleSetDto>>(HttpMethod.Get, "/v1/risk/rules", null,
            ("X-Api-Key", second.ApiKey))).ShouldBeEmpty();
        (await SendOk<List<BlocklistDto>>(HttpMethod.Get, "/v1/risk/blocklist", null,
            ("X-Api-Key", second.ApiKey))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Karar_defteri_degistirilemez_olmali()
    {
        var tenant = await SeedTenantAsync();
        await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 30_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));

        await using var db = _fixture.CreateRisk(PostgresFixture.TenantCtx(tenant.TenantId));
        var assessment = await db.RiskAssessments.SingleAsync();
        assessment.Outcome.ShouldBe("allow"); // kural yok → izin verildi

        // Uygulama rolünün UPDATE/DELETE yetkisi YOKTUR — "neden engellendim"
        // sorusunun cevabı sonradan değiştirilemez. Değer GERÇEKTEN farklı olmalı,
        // yoksa EF hiç UPDATE üretmez ve test yanlışlıkla yeşil yanardı.
        assessment.GetType()
            .GetProperty(nameof(Poyra.Modules.Risk.Domain.RiskAssessment.Outcome))!
            .SetValue(assessment, "block");

        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        error.InnerException.ShouldBeOfType<Npgsql.PostgresException>().SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task Kural_seti_silinemez_olmali()
    {
        var tenant = await SeedTenantAsync();
        await PublishAsync(tenant.ApiKey, """{"rules":[],"default":"allow"}""");

        await using var db = _fixture.CreateRisk(PostgresFixture.TenantCtx(tenant.TenantId));
        db.RiskRuleSets.RemoveRange(await db.RiskRuleSets.ToListAsync());

        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        error.InnerException.ShouldBeOfType<Npgsql.PostgresException>().SqlState.ShouldBe("42501");
    }

    // ---- Rol kapısı -------------------------------------------------------------------

    [Fact]
    public async Task Risk_kurali_yayinlamak_admin_istemeli()
    {
        const string password = "risk-parola-1234";
        var email = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Risk Rol A.Ş.",
            slug = "rsk-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = password,
            ownerName = "Sahip",
        }, ("X-Platform-Key", AdminKey));

        var opsEmail = $"ops-{Guid.NewGuid():N}@ornek.com";
        await SendOk<object>(HttpMethod.Post, "/v1/users",
            new { email = opsEmail, password, displayName = "Operasyon", role = "operations" },
            ("X-Api-Key", tenant.ApiKey));

        var login = await SendOk<Dictionary<string, System.Text.Json.JsonElement>>(
            HttpMethod.Post, "/v1/auth/login",
            new { email = opsEmail, password, tenantSlug = tenant.Slug });
        var token = login["accessToken"].GetString()!;

        // Risk kuralı tüm tahsilatı durdurabilir — rota kuralıyla aynı eşik: admin
        var publish = await Send(HttpMethod.Post, "/v1/risk/rules",
            new { document = """{"rules":[],"default":"block"}""" },
            ("Authorization", $"Bearer {token}"));
        publish.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Okumak serbest: operasyon ekibi hangi kararın verildiğini görebilmeli
        var read = await Send(HttpMethod.Get, "/v1/risk/assessments", null,
            ("Authorization", $"Bearer {token}"));
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
