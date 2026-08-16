using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Poyra.Api;
using Poyra.Modules.Compliance.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// M20 Uyum ve denetim. Denetim izinin iki özelliği vardır ve ikisi de test edilir:
/// <b>eksiksiz</b> olmalı (eylem yapılıp deftere düşmemesi, defteri işe yaramaz kılar)
/// ve <b>değiştirilemez</b> olmalı (düzeltilebilen iz, iz değildir).
/// </summary>
[Collection("postgres")]
public sealed class ComplianceFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "uyum-parola-1234";

    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _api;

    public ComplianceFlowTests(PostgresFixture fixture)
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
    private sealed record AuditDto(
        Guid Id, string Actor, string Action, string ResourceType, string? ResourceId,
        string Summary, string Metadata, string? IpAddress, DateTimeOffset CreatedAt);
    private sealed record ReportDto(
        string Id, string PaymentIds, string? CustomerRef, string Rationale,
        string Status, string? Resolution, DateTimeOffset? ResolvedAt, DateTimeOffset CreatedAt);
    private sealed record AccountDto(Guid Id, string Label);
    private sealed record AgentDto(Guid Id, string Code, bool Active);

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

    private async Task<(TenantCreated Tenant, string OwnerEmail)> SeedAsync()
    {
        var email = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Uyum A.Ş.",
            slug = "uym-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "Sahip",
        }, ("X-Platform-Key", AdminKey));

        return (tenant, email);
    }

    private Task<List<AuditDto>> AuditAsync(string apiKey, string? query = null)
        => SendOk<List<AuditDto>>(HttpMethod.Get, "/v1/compliance/audit" + query, null, ("X-Api-Key", apiKey));

    // ---- Defterin eksiksizliği --------------------------------------------------------

    [Fact]
    public async Task Degistirici_eylemler_deftere_dusmeli()
    {
        var (tenant, _) = await SeedAsync();

        await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        await SendOk<object>(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "ip", value = "203.0.113.5", reason = "kart deneme" }, ("X-Api-Key", tenant.ApiKey));

        var audit = await AuditAsync(tenant.ApiKey);

        audit.ShouldContain(e => e.Action == "connector_account.created");
        audit.ShouldContain(e => e.Action == "blocklist_entry.created");
        audit.ShouldAllBe(e => e.Actor == "api_key"); // makine çağrısı
    }

    [Fact]
    public async Task Okuma_deftere_DUSMEMELI()
    {
        var (tenant, _) = await SeedAsync();
        await AuditAsync(tenant.ApiKey);
        await SendOk<List<object>>(HttpMethod.Get, "/v1/connector-accounts", null, ("X-Api-Key", tenant.ApiKey));

        // GET bir eylem değildir; yazsaydı defter gürültüden okunamaz hale gelirdi
        (await AuditAsync(tenant.ApiKey)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Yetkisiz_deneme_deftere_DUSMELI()
    {
        var (tenant, _) = await SeedAsync();
        var auditorEmail = $"denetci-{Guid.NewGuid():N}@ornek.com";
        await SendOk<object>(HttpMethod.Post, "/v1/users",
            new { email = auditorEmail, password = Password, displayName = "Denetçi", role = "auditor" },
            ("X-Api-Key", tenant.ApiKey));

        var login = await SendOk<Dictionary<string, System.Text.Json.JsonElement>>(
            HttpMethod.Post, "/v1/auth/login",
            new { email = auditorEmail, password = Password, tenantSlug = tenant.Slug });
        var token = login["accessToken"].GetString()!;

        // Denetçi POS eklemeye çalışıyor → 403
        var denied = await Send(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Yetkisiz",
            credentials = new Dictionary<string, string> { ["secret"] = "x" },
        }, ("Authorization", $"Bearer {token}"));
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // ★ "Kim neyi yapmaya çalıştı" bir güvenlik sorusudur — reddedilen deneme yazılır
        var audit = await AuditAsync(tenant.ApiKey);
        var attempt = audit.Single(e => e.Action == "connector_account.denied");
        attempt.Actor.ShouldStartWith("user:");
        attempt.Summary.ShouldContain("YETKİSİZ");
    }

    [Fact]
    public async Task Basarisiz_dogrulama_defteri_kirletmemeli()
    {
        var (tenant, _) = await SeedAsync();

        // Yanlış yazılmış istek bir eylem değildir
        var bad = await Send(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "ip", value = "1.2.3.4", reason = "" }, ("X-Api-Key", tenant.ApiKey));
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await AuditAsync(tenant.ApiKey)).ShouldNotContain(e => e.ResourceType == "blocklist_entry");
    }

    [Fact]
    public async Task Kullanici_eylemi_aktoru_ile_yazilmali()
    {
        var (tenant, ownerEmail) = await SeedAsync();

        var login = await SendOk<Dictionary<string, System.Text.Json.JsonElement>>(
            HttpMethod.Post, "/v1/auth/login",
            new { email = ownerEmail, password = Password, tenantSlug = tenant.Slug });
        var token = login["accessToken"].GetString()!;
        var userId = login["userId"].GetGuid();

        await SendOk<object>(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "customer_ref", value = "kotu-musteri", reason = "sahtecilik" },
            ("Authorization", $"Bearer {token}"));

        var audit = await AuditAsync(tenant.ApiKey);
        audit.Single(e => e.Action == "blocklist_entry.created").Actor.ShouldBe($"user:{userId}");
    }

    [Fact]
    public async Task Ekstre_yukleme_ve_oran_degisikligi_deftere_dusmeli()
    {
        // Gözden geçirmede bulunan boşluk: /v1/settlements ve /v1/ledger izlenmiyordu —
        // ekstre yüklemek de finansman oranını değiştirmek de defterde iz bırakmıyordu.
        var (tenant, _) = await SeedAsync();

        var account = await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Defter POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(
            "value_date;amount;description;reference\n2026-08-13;100000;POS HASILAT;REF1\n"));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        body.Add(file, "file", "hesap-ekstresi.csv");
        body.Add(new StringContent(account.Id.ToString()), "connectorAccountId");
        body.Add(new StringContent("poyra_csv"), "format");

        var upload = new HttpRequestMessage(HttpMethod.Post, "/v1/settlements/upload") { Content = body };
        upload.Headers.Add("X-Api-Key", tenant.ApiKey);
        var uploaded = await _api.SendAsync(upload);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.OK, await uploaded.Content.ReadAsStringAsync());

        await SendOk<object>(HttpMethod.Post, "/v1/ledger/settings",
            new { annualFinancingRateBps = 3500 }, ("X-Api-Key", tenant.ApiKey));

        var audit = await AuditAsync(tenant.ApiKey);
        audit.ShouldContain(e => e.Action == "settlement.upload");
        audit.ShouldContain(e => e.Action == "ledger.settings");

        // Denetçinin yükleme DENEMESİ de yazılmalı — "kim neyi yapmaya çalıştı"
        var auditorEmail = $"denetci-{Guid.NewGuid():N}@ornek.com";
        await SendOk<object>(HttpMethod.Post, "/v1/users",
            new { email = auditorEmail, password = Password, displayName = "Denetçi", role = "auditor" },
            ("X-Api-Key", tenant.ApiKey));
        var login = await SendOk<Dictionary<string, System.Text.Json.JsonElement>>(
            HttpMethod.Post, "/v1/auth/login",
            new { email = auditorEmail, password = Password, tenantSlug = tenant.Slug });
        var token = login["accessToken"].GetString()!;

        var denied = await Send(HttpMethod.Post, "/v1/settlements/upload", new { },
            ("Authorization", $"Bearer {token}"));
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var attempt = (await AuditAsync(tenant.ApiKey)).Single(e => e.Action == "settlement.denied");
        attempt.Actor.ShouldStartWith("user:");
        attempt.Summary.ShouldContain("YETKİSİZ");
    }

    [Fact]
    public async Task Saha_temsilcisi_yasam_dongusu_deftere_dusmeli()
    {
        // Temsilci açmak/kapatmak ve cihaz bırakmak, kimin şirket adına para
        // toplayabileceğini değiştirir — üçü de deftere düşmeli.
        var (tenant, _) = await SeedAsync();

        var agent = await SendOk<AgentDto>(HttpMethod.Post, "/v1/field/agents",
            new { code = "TMS-001", name = "Saha Temsilcisi" }, ("X-Api-Key", tenant.ApiKey));

        await SendOk<AgentDto>(HttpMethod.Post, $"/v1/field/agents/{agent.Id}/release-device",
            new { reason = "telefon kayboldu" }, ("X-Api-Key", tenant.ApiKey));

        await SendOk<AgentDto>(HttpMethod.Post, $"/v1/field/agents/{agent.Id}/disable",
            new { reason = "işten ayrıldı" }, ("X-Api-Key", tenant.ApiKey));

        var audit = await AuditAsync(tenant.ApiKey);
        audit.ShouldContain(e => e.Action == "field_agent.created");
        audit.ShouldContain(e => e.Action == "field_agent.release_device");
        audit.ShouldContain(e => e.Action == "field_agent.disable");

        // Hangi temsilciye dokunulduğu yoldan çözülür
        audit.Single(e => e.Action == "field_agent.disable").ResourceId
            .ShouldBe(agent.Id.ToString());
    }

    // ---- Değiştirilemezlik ------------------------------------------------------------

    [Fact]
    public async Task Denetim_defteri_DEGISTIRILEMEZ_olmali()
    {
        var (tenant, _) = await SeedAsync();
        await SendOk<object>(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "ip", value = "203.0.113.9", reason = "test" }, ("X-Api-Key", tenant.ApiKey));

        await using var db = _fixture.CreateCompliance(PostgresFixture.TenantCtx(tenant.TenantId));
        var entry = await db.AuditLog.FirstAsync();
        entry.Summary.ShouldNotBe("değiştirildi");

        // Düzeltilebilen iz, iz değildir
        entry.GetType().GetProperty(nameof(AuditLogEntry.Summary))!.SetValue(entry, "değiştirildi");
        var update = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        update.InnerException.ShouldBeOfType<Npgsql.PostgresException>().SqlState.ShouldBe("42501");

        db.ChangeTracker.Clear();
        db.AuditLog.RemoveRange(await db.AuditLog.ToListAsync());
        var delete = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        delete.InnerException.ShouldBeOfType<Npgsql.PostgresException>().SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task Baska_isyerinin_defteri_gorunmemeli()
    {
        var (first, _) = await SeedAsync();
        var (second, _) = await SeedAsync();

        await SendOk<object>(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "ip", value = "203.0.113.11", reason = "ilk işyeri" }, ("X-Api-Key", first.ApiKey));

        (await AuditAsync(second.ApiKey)).ShouldBeEmpty();
        (await AuditAsync(first.ApiKey)).ShouldNotBeEmpty();
    }

    // ---- Dışa aktarma -------------------------------------------------------------------

    [Fact]
    public async Task CSV_disa_aktarilabilmeli()
    {
        var (tenant, _) = await SeedAsync();
        await SendOk<object>(HttpMethod.Post, "/v1/risk/blocklist",
            new { kind = "ip", value = "203.0.113.13", reason = "dışa aktarma testi" },
            ("X-Api-Key", tenant.ApiKey));

        var response = await Send(HttpMethod.Get, "/v1/compliance/audit/export", null,
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        // UTF-8 BOM: Excel BOM'suz dosyada Türkçe karakterleri bozar
        bytes[0].ShouldBe((byte)0xEF);
        bytes[1].ShouldBe((byte)0xBB);
        bytes[2].ShouldBe((byte)0xBF);

        var csv = Encoding.UTF8.GetString(bytes);
        csv.ShouldContain("zaman_tr;aktor;eylem"); // ayraç ; — Excel TR'de virgül ondalıktır
        csv.ShouldContain("blocklist_entry.created");
    }

    // ---- Şüpheli işlem incelemesi -------------------------------------------------------

    [Fact]
    public async Task Supheli_islem_incelemesi_acilip_sonuclandirilabilmeli()
    {
        var (tenant, _) = await SeedAsync();

        var report = await SendOk<ReportDto>(HttpMethod.Post, "/v1/compliance/reports", new
        {
            paymentIds = new[] { "pay_ornek1", "pay_ornek2" },
            customerRef = "cust-99",
            rationale = "Kısa sürede çok sayıda farklı kartla yüksek tutarlı işlem.",
        }, ("X-Api-Key", tenant.ApiKey));

        report.Id.ShouldStartWith("sar_");
        report.Status.ShouldBe("under_review");
        report.PaymentIds.ShouldBe("pay_ornek1,pay_ornek2");

        var resolved = await SendOk<ReportDto>(HttpMethod.Post,
            $"/v1/compliance/reports/{report.Id}/resolve",
            new { status = "ready_to_file", resolution = "MASAK bildirimi hazırlanacak." },
            ("X-Api-Key", tenant.ApiKey));

        resolved.Status.ShouldBe("ready_to_file");
        resolved.ResolvedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Gerekcesiz_karar_reddedilmeli()
    {
        var (tenant, _) = await SeedAsync();
        var report = await SendOk<ReportDto>(HttpMethod.Post, "/v1/compliance/reports",
            new { paymentIds = new[] { "pay_x" }, rationale = "Şüpheli hareket." },
            ("X-Api-Key", tenant.ApiKey));

        // Gerekçesiz karar denetimde savunulamaz
        var response = await Send(HttpMethod.Post, $"/v1/compliance/reports/{report.Id}/resolve",
            new { status = "dismissed", resolution = "" }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Gerekcesiz_kayit_acilamamali()
    {
        var (tenant, _) = await SeedAsync();

        var response = await Send(HttpMethod.Post, "/v1/compliance/reports",
            new { paymentIds = new[] { "pay_x" }, rationale = "" }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sonuclanmis_kayit_yeniden_sonuclandirilamamali()
    {
        var (tenant, _) = await SeedAsync();
        var report = await SendOk<ReportDto>(HttpMethod.Post, "/v1/compliance/reports",
            new { paymentIds = new[] { "pay_y" }, rationale = "Şüpheli." }, ("X-Api-Key", tenant.ApiKey));

        await SendOk<ReportDto>(HttpMethod.Post, $"/v1/compliance/reports/{report.Id}/resolve",
            new { status = "dismissed", resolution = "Bildirime gerek yok." }, ("X-Api-Key", tenant.ApiKey));

        var again = await Send(HttpMethod.Post, $"/v1/compliance/reports/{report.Id}/resolve",
            new { status = "filed", resolution = "Fikir değişti." }, ("X-Api-Key", tenant.ApiKey));

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).ShouldContain("compliance.already_resolved");
    }

    // ---- Rol kapısı -----------------------------------------------------------------------

    [Fact]
    public async Task Denetci_uyum_defterini_gorebilmeli_ve_kayit_acabilmeli()
    {
        var (tenant, _) = await SeedAsync();
        var auditorEmail = $"denetci-{Guid.NewGuid():N}@ornek.com";
        await SendOk<object>(HttpMethod.Post, "/v1/users",
            new { email = auditorEmail, password = Password, displayName = "Uyum", role = "auditor" },
            ("X-Api-Key", tenant.ApiKey));

        var login = await SendOk<Dictionary<string, System.Text.Json.JsonElement>>(
            HttpMethod.Post, "/v1/auth/login",
            new { email = auditorEmail, password = Password, tenantSlug = tenant.Slug });
        var token = login["accessToken"].GetString()!;

        // Uyum görevlisi PARA HAREKETİ yapmadan çalışabilmeli
        (await Send(HttpMethod.Get, "/v1/compliance/audit", null, ("Authorization", $"Bearer {token}")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Send(HttpMethod.Post, "/v1/compliance/reports",
                new { paymentIds = new[] { "pay_z" }, rationale = "İnceleme." },
                ("Authorization", $"Bearer {token}")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...ama POS ekleyemez
        (await Send(HttpMethod.Post, "/v1/connector-accounts",
                new { connectorKey = "mockbank", label = "x", credentials = new Dictionary<string, string>() },
                ("Authorization", $"Bearer {token}")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
