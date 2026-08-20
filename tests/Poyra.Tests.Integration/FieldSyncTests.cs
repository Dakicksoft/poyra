using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Api;
using Poyra.Checkout;
using Poyra.SharedKernel.Tenancy;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// M17 Saha — çevrimdışı kuyruğun sunucu tarafı.
///
/// Buradaki testlerin ortak sorusu tek: <b>cihaz otorite olabiliyor mu?</b> Olamamalı
/// (İlke 2). Cihaz beyan eder — zamanı, tutarı, yöntemi — ama yasal zaman ve para durumu
/// sunucudan doğar. Ağ kopmaları, saat bozuklukları ve tekrar gönderimler bu sınırı
/// zorlayan gerçek saha koşullarıdır.
/// </summary>
[Collection("postgres")]
public sealed class FieldSyncTests : IDisposable
{
    private const string AdminKey = "test-admin-key";

    private readonly PostgresFixture _fixture;

    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly WebApplicationFactory<CheckoutEntryPoint> _checkoutFactory;
    private readonly HttpClient _api;
    private readonly HttpClient _apiNoRedirect; // banka dönüşü 302 verir
    private readonly HttpClient _checkout;

    public FieldSyncTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        void Configure(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Platform:AdminKey", AdminKey);
            builder.UseSetting("Poyra:PublicBaseUrl", "http://localhost");
            builder.UseSetting("Poyra:CheckoutBaseUrl", "http://localhost");
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Poyra:JwtKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            builder.UseSetting("Poyra:VaultKey", Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
        }

        _factory = new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(Configure);
        _checkoutFactory = new WebApplicationFactory<CheckoutEntryPoint>().WithWebHostBuilder(Configure);
        _api = _factory.CreateClient();
        _apiNoRedirect = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _checkout = _checkoutFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _checkoutFactory.Dispose();
    }

    // ------------------------------------------------------------------ yardımcılar

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record AgentDto(Guid Id, string Code, string Name, string? Region,
        string? DeviceId, bool PinSet, DateTimeOffset? EnrolledAt, DateTimeOffset? LastSyncAt,
        bool Active, string? DisabledReason);
    private sealed record SyncResult(Guid ClientOpId, string Outcome, string? CollectionId,
        string? Status, string? CheckoutUrl, string? Reason);
    private sealed record SyncResponse(DateTimeOffset ServerTime, Guid AgentId,
        int Accepted, int Duplicate, int Rejected, List<SyncResult> Results);
    private sealed record CollectionDto(string Id, Guid AgentId, string AgentCode,
        string? CustomerRef, long AmountMinor, string Currency, string Method, string Status,
        string? CheckoutUrl, string? PaymentId, DateTimeOffset CapturedAtDevice,
        DateTimeOffset OccurredAtServer, long DeviceSkewSeconds,
        double? Latitude, double? Longitude, string? Note);
    private sealed record SettlementRow(Guid AgentId, string AgentCode, string? Region,
        int Count, long DeclaredCashMinor, long SettledMinor, long PendingMinor,
        long FailedMinor, int SkewedCount);
    private sealed record SettlementReport(DateOnly Day, long TotalDeclaredCashMinor,
        long TotalSettledMinor, List<SettlementRow> Agents);

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

    private async Task<(TenantCreated Tenant, AgentDto Agent)> SeedAsync()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Saha A.Ş.",
            slug = "saha-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = $"sahip-{Guid.NewGuid():N}@ornek.com",
            ownerPassword = "saha-parola-123",
            ownerName = "Sahip",
        }, ("X-Platform-Key", AdminKey));

        await SendOk<object>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        var agent = await SendOk<AgentDto>(HttpMethod.Post, "/v1/field/agents", new
        {
            code = "BAYI-01",
            name = "Ahmet Yılmaz",
            region = "İstanbul Avrupa",
        }, ("X-Api-Key", tenant.ApiKey));

        return (tenant, agent);
    }

    /// <summary>
    /// Gerçek bir TR cihazının gönderdiği zaman: <c>+03:00</c> ofsetli.
    /// Testleri UTC ile yazmak, Npgsql'in "yalnız sıfır ofset" kuralını gizler —
    /// bu tam olarak yaşandı: modül testlerde çalışıyor, sahada 500 dönüyordu.
    /// </summary>
    private static DateTimeOffset TrNow => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));

    private static object Op(Guid id, string method, long amount, DateTimeOffset captured,
        string? customerRef = null) => new
    {
        clientOpId = id,
        method,
        amountMinor = amount,
        currency = "TRY",
        capturedAtDevice = captured,
        customerRef,
        description = (string?)null,
        note = (string?)null,
        latitude = (double?)null,
        longitude = (double?)null,
    };

    // ------------------------------------------------------------------ İLKE 2

    [Fact]
    public async Task Cihaz_saati_BEYANDIR_yasal_zaman_sunucudan_yazilir()
    {
        var (tenant, _) = await SeedAsync();

        // Temsilcinin telefonu bir yıl geride: bu kayıt 2025'te alınmış görünüyor
        var deviceTime = new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.FromHours(3));
        var before = TrNow;

        var sync = await SendOk<SyncResponse>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "bayi-01",
            deviceId = "cihaz-A",
            operations = new[] { Op(Guid.NewGuid(), "cash_declared", 15_000, deviceTime) },
        }, ("X-Api-Key", tenant.ApiKey));

        sync.Accepted.ShouldBe(1);

        var rows = await SendOk<List<CollectionDto>>(
            HttpMethod.Get, "/v1/field/collections", null, ("X-Api-Key", tenant.ApiKey));
        var row = rows.ShouldHaveSingleItem();

        // Beyan olduğu gibi durur — düzeltmek delili yok etmektir
        row.CapturedAtDevice.ShouldBe(deviceTime);

        // Yasal zaman SUNUCUDAN: cihazın 2025 iddiası kaydın zamanı OLMADI
        row.OccurredAtServer.ShouldBeGreaterThanOrEqualTo(before);
        row.OccurredAtServer.Year.ShouldNotBe(2025);

        // Fark kanıt olarak saklanır (~1 yıldan fazla)
        row.DeviceSkewSeconds.ShouldBeGreaterThan(300L * 86400);
    }

    [Fact]
    public async Task Cihaz_succeeded_URETEMEZ_nakit_yalnizca_beyandir()
    {
        var (tenant, _) = await SeedAsync();

        var sync = await SendOk<SyncResponse>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "BAYI-01",
            deviceId = "cihaz-A",
            operations = new[]
            {
                Op(Guid.NewGuid(), "cash_declared", 50_000, TrNow),
                Op(Guid.NewGuid(), "link", 25_000, TrNow),
            },
        }, ("X-Api-Key", tenant.ApiKey));

        // Hiçbir işlem 'succeeded' doğmadı — para durumu sunucunun ödeme akışından gelir
        sync.Results.ShouldAllBe(r => r.Status != "succeeded");

        var byStatus = sync.Results.ToLookup(r => r.Status);
        byStatus["cash_declared"].Count().ShouldBe(1);
        byStatus["link_issued"].Count().ShouldBe(1);

        // Bağlantılı kayıt müşteriye gösterilecek adresi taşır; nakit taşımaz
        sync.Results.Single(r => r.Status == "link_issued").CheckoutUrl.ShouldNotBeNullOrEmpty();
        sync.Results.Single(r => r.Status == "cash_declared").CheckoutUrl.ShouldBeNull();
    }

    // ------------------------------------------------------------------ çevrimdışı kuyruk

    [Fact]
    public async Task Ayni_parti_yeniden_gonderilirse_IKINCI_kayit_ACILMAZ()
    {
        var (tenant, _) = await SeedAsync();
        var opId = Guid.NewGuid();
        var captured = TrNow.AddMinutes(-30);

        object Batch() => new
        {
            agentCode = "BAYI-01",
            deviceId = "cihaz-A",
            operations = new[] { Op(opId, "link", 30_000, captured) },
        };

        // Gerçek senaryo: sunucu kaydetti, ama onay cihaza ulaşmadan ağ koptu.
        // Cihaz aynı partiyi tekrar gönderir.
        var first = await SendOk<SyncResponse>(
            HttpMethod.Post, "/v1/field/sync", Batch(), ("X-Api-Key", tenant.ApiKey));
        var second = await SendOk<SyncResponse>(
            HttpMethod.Post, "/v1/field/sync", Batch(), ("X-Api-Key", tenant.ApiKey));

        first.Accepted.ShouldBe(1);
        second.Accepted.ShouldBe(0);
        second.Duplicate.ShouldBe(1);

        // İLK kaydın kimliği geri döner — cihaz kuyruğunu doğru eşleştirebilsin
        second.Results[0].CollectionId.ShouldBe(first.Results[0].CollectionId);

        // Ve en önemlisi: müşteriye İKİNCİ bir ödeme talebi gitmedi
        var rows = await SendOk<List<CollectionDto>>(
            HttpMethod.Get, "/v1/field/collections", null, ("X-Api-Key", tenant.ApiKey));
        rows.Count.ShouldBe(1);
        second.Results[0].CheckoutUrl.ShouldBe(first.Results[0].CheckoutUrl);
    }

    [Fact]
    public async Task Gecersiz_TEK_kayit_partinin_geri_kalanini_DUSURMEMELI()
    {
        var (tenant, _) = await SeedAsync();

        // Zehirli kayıt: parti tümüyle reddedilseydi cihazın kuyruğu sonsuza dek
        // tıkanır ve o günün TÜM tahsilatları sunucuya hiç ulaşamazdı.
        var sync = await SendOk<SyncResponse>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "BAYI-01",
            deviceId = "cihaz-A",
            operations = new[]
            {
                Op(Guid.NewGuid(), "cash_declared", 10_000, TrNow),
                Op(Guid.NewGuid(), "gokten_zembille", 20_000, TrNow), // bilinmeyen yöntem
                Op(Guid.NewGuid(), "cash_declared", -5, TrNow),        // negatif tutar
                Op(Guid.NewGuid(), "cash_declared", 30_000, TrNow),
            },
        }, ("X-Api-Key", tenant.ApiKey));

        sync.Accepted.ShouldBe(2);
        sync.Rejected.ShouldBe(2);

        // Ret gerekçeli olmalı: temsilci neyin neden gitmediğini görebilmeli
        sync.Results.Where(r => r.Outcome == "rejected")
            .ShouldAllBe(r => r.Reason != null && r.Reason.Length > 5);

        // Sağlam kayıtlar gerçekten yazıldı
        var rows = await SendOk<List<CollectionDto>>(
            HttpMethod.Get, "/v1/field/collections", null, ("X-Api-Key", tenant.ApiKey));
        rows.Sum(r => r.AmountMinor).ShouldBe(40_000);
    }

    [Fact]
    public async Task Sonuclar_cihazin_gonderdigi_SIRAYLA_donmeli()
    {
        var (tenant, _) = await SeedAsync();
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();

        var sync = await SendOk<SyncResponse>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "BAYI-01",
            deviceId = "cihaz-A",
            operations = ids.Select((id, i) =>
                Op(id, i == 2 ? "olmayan" : "cash_declared", 1_000 * (i + 1), TrNow)).ToArray(),
        }, ("X-Api-Key", tenant.ApiKey));

        sync.Results.Select(r => r.ClientOpId).ShouldBe(ids);
    }

    // ------------------------------------------------------------------ cihaz bağlama

    [Fact]
    public async Task Baska_cihazdan_senkron_REDDEDILMELI_ve_serbest_birakma_cozmeli()
    {
        var (tenant, agent) = await SeedAsync();

        object Batch(string device) => new
        {
            agentCode = "BAYI-01",
            deviceId = device,
            operations = new[] { Op(Guid.NewGuid(), "cash_declared", 1_000, TrNow) },
        };

        await SendOk<SyncResponse>(HttpMethod.Post, "/v1/field/sync", Batch("cihaz-A"),
            ("X-Api-Key", tenant.ApiKey));

        // Çalınan/klonlanan cihaz aynı kodla kuyruk gönderemez
        var intruder = await Send(HttpMethod.Post, "/v1/field/sync", Batch("cihaz-B"),
            ("X-Api-Key", tenant.ApiKey));
        intruder.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Telefon değişimi YÖNETİCİ kararıdır
        var released = await SendOk<AgentDto>(
            HttpMethod.Post, $"/v1/field/agents/{agent.Id}/release-device",
            new { reason = "Telefon değişti" }, ("X-Api-Key", tenant.ApiKey));
        released.DeviceId.ShouldBeNull();

        await SendOk<SyncResponse>(HttpMethod.Post, "/v1/field/sync", Batch("cihaz-B"),
            ("X-Api-Key", tenant.ApiKey));
    }

    [Fact]
    public async Task Kapatilan_temsilcinin_senkronu_reddedilmeli_ama_gecmisi_KALMALI()
    {
        var (tenant, agent) = await SeedAsync();

        await SendOk<SyncResponse>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "BAYI-01",
            deviceId = "cihaz-A",
            operations = new[] { Op(Guid.NewGuid(), "cash_declared", 75_000, TrNow) },
        }, ("X-Api-Key", tenant.ApiKey));

        await SendOk<AgentDto>(HttpMethod.Post, $"/v1/field/agents/{agent.Id}/disable",
            new { reason = "İşten ayrıldı" }, ("X-Api-Key", tenant.ApiKey));

        var after = await Send(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "BAYI-01",
            deviceId = "cihaz-A",
            operations = new[] { Op(Guid.NewGuid(), "cash_declared", 1_000, TrNow) },
        }, ("X-Api-Key", tenant.ApiKey));
        after.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // İLKE 3: temsilci silinmedi — "bu parayı kim topladı" hâlâ cevaplanabilir
        var agents = await SendOk<List<AgentDto>>(
            HttpMethod.Get, "/v1/field/agents", null, ("X-Api-Key", tenant.ApiKey));
        var disabled = agents.Single(a => a.Id == agent.Id);
        disabled.Active.ShouldBeFalse();
        disabled.DisabledReason.ShouldBe("İşten ayrıldı");

        var rows = await SendOk<List<CollectionDto>>(
            HttpMethod.Get, "/v1/field/collections", null, ("X-Api-Key", tenant.ApiKey));
        rows.ShouldHaveSingleItem().AgentCode.ShouldBe("bayi-01");
    }

    // ------------------------------------------------------------------ gün sonu

    [Fact]
    public async Task Gun_sonu_ozeti_NAKIT_ile_karti_AYRI_toplamali()
    {
        var (tenant, _) = await SeedAsync();

        await SendOk<SyncResponse>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "BAYI-01",
            deviceId = "cihaz-A",
            operations = new[]
            {
                Op(Guid.NewGuid(), "cash_declared", 100_000, TrNow),
                Op(Guid.NewGuid(), "cash_declared", 50_000, TrNow),
                Op(Guid.NewGuid(), "link", 80_000, TrNow),
            },
        }, ("X-Api-Key", tenant.ApiKey));

        var report = await SendOk<SettlementReport>(
            HttpMethod.Get, "/v1/field/settlement-report", null, ("X-Api-Key", tenant.ApiKey));

        var row = report.Agents.ShouldHaveSingleItem();
        row.Count.ShouldBe(3);

        // Nakit BEYAN: temsilcinin kasaya teslim etmesi gereken meblağ
        row.DeclaredCashMinor.ShouldBe(150_000);

        // Bağlantı üretildi ama HENÜZ ÖDENMEDİ — tahsil edilmiş sayılamaz
        row.PendingMinor.ShouldBe(80_000);
        row.SettledMinor.ShouldBe(0);

        // Ve toplamlar birbirine karışmaz: nakdi tahsilata eklemek, hiç görmediğimiz
        // parayı gerçekten toplanmış göstermek olurdu
        report.TotalDeclaredCashMinor.ShouldBe(150_000);
        report.TotalSettledMinor.ShouldBe(0);
    }

    [Fact]
    public async Task Rapor_bozuk_cihaz_saatini_GORUNUR_kilmali()
    {
        var (tenant, _) = await SeedAsync();

        await SendOk<SyncResponse>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "BAYI-01",
            deviceId = "cihaz-A",
            operations = new[]
            {
                Op(Guid.NewGuid(), "cash_declared", 1_000, TrNow),
                // Fabrika ayarına dönmüş telefon
                Op(Guid.NewGuid(), "cash_declared", 2_000, DateTimeOffset.UnixEpoch),
            },
        }, ("X-Api-Key", tenant.ApiKey));

        var report = await SendOk<SettlementReport>(
            HttpMethod.Get, "/v1/field/settlement-report", null, ("X-Api-Key", tenant.ApiKey));

        // İkisi de KAYDEDİLDİ (beyanı reddetmek sahadaki tahsilatı yok saymaktır)
        // ama saati bozuk olan sayılıyor — denetim nereye bakacağını bilsin
        report.Agents.ShouldHaveSingleItem().Count.ShouldBe(2);
        report.Agents[0].SkewedCount.ShouldBe(1);
    }

    // ------------------------------------------------------------------ işyeri yalıtımı

    [Fact]
    public async Task Baska_isyerinin_saha_kayitlari_GORUNMEMELI()
    {
        var (first, _) = await SeedAsync();
        var (second, _) = await SeedAsync();

        await SendOk<SyncResponse>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "BAYI-01",
            deviceId = "cihaz-A",
            operations = new[] { Op(Guid.NewGuid(), "cash_declared", 99_000, TrNow) },
        }, ("X-Api-Key", first.ApiKey));

        var otherRows = await SendOk<List<CollectionDto>>(
            HttpMethod.Get, "/v1/field/collections", null, ("X-Api-Key", second.ApiKey));
        otherRows.ShouldBeEmpty();

        // Aynı temsilci KODU iki işyerinde ayrı ayrı var olabilir ve karışmaz
        var otherAgents = await SendOk<List<AgentDto>>(
            HttpMethod.Get, "/v1/field/agents", null, ("X-Api-Key", second.ApiKey));
        otherAgents.ShouldHaveSingleItem().Code.ShouldBe("bayi-01");
    }

    // ------------------------------------------------------------------ İlke 2'nin ikinci yarısı

    [Fact]
    public async Task Para_durumunu_SUNUCU_yazar_cihaz_degil()
    {
        // Bu test M17'nin varlık sebebini sınar. Cihaz "tahsil edildi" diyemez —
        // o hâlde bu durumu kim yazacak? FieldOutcomeJob yazar ve bunu temsilcinin
        // telefonu kapalıyken bile yapabilmelidir.
        var (tenant, _) = await SeedAsync();

        var sync = await SendOk<SyncResponse>(HttpMethod.Post, "/v1/field/sync", new
        {
            agentCode = "BAYI-01",
            deviceId = "cihaz-A",
            operations = new[] { Op(Guid.NewGuid(), "link", 14_900, TrNow) },
        }, ("X-Api-Key", tenant.ApiKey));

        var issued = sync.Results.ShouldHaveSingleItem();
        issued.Status.ShouldBe("link_issued"); // bağlantı üretildi — para HENÜZ gelmedi
        issued.CheckoutUrl.ShouldNotBeNullOrEmpty();

        // İş şimdi koşsa bir şey DEĞİŞMEMELİ: ödenmemiş bağlantı succeeded olamaz
        await RunOutcomeJobAsync();
        (await CollectionsAsync(tenant.ApiKey)).ShouldHaveSingleItem().Status.ShouldBe("link_issued");

        // Müşteri bağlantıyı GERÇEKTEN öder (checkout → banka → callback)
        var slug = issued.CheckoutUrl!.Split("/l/")[1];
        var paymentId = await PayThroughCheckoutAsync(slug);

        // Cihaz bu sırada hiçbir şey göndermedi — kapalı bile olabilir.
        // Müşteri de sonuç sayfasını AÇMADI: durumu tamamen sunucu kapatmalı.
        await RunLinkOutcomeJobAsync();
        await RunOutcomeJobAsync();

        var settled = (await CollectionsAsync(tenant.ApiKey)).ShouldHaveSingleItem();
        settled.Status.ShouldBe("succeeded");
        settled.PaymentId.ShouldBe(paymentId);

        // Saha tahsilatı checkout'tan ödendi ama rota için AYRI bir kanaldır: bağlantının
        // kaynağı (PaymentLink.Origin) ödemenin kanalına taşınmalı, yoksa saha tahsilatı
        // normal ödeme linkinden ayırt edilemez ve "saha → şu POS" kuralı yazılamaz.
        await using (var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId)))
        {
            var intent = await db.PaymentIntents.AsNoTracking()
                .SingleAsync(i => i.PublicId == paymentId);
            intent.Channel.ShouldBe("field");
        }

        // Ve gün sonu özetinde artık 'kesinleşen' tarafında
        var report = await SendOk<SettlementReport>(
            HttpMethod.Get, "/v1/field/settlement-report", null, ("X-Api-Key", tenant.ApiKey));
        report.Agents.ShouldHaveSingleItem().SettledMinor.ShouldBe(14_900);
        report.Agents[0].PendingMinor.ShouldBe(0);
    }

    private async Task RunOutcomeJobAsync()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();
        await scope.ServiceProvider
            .GetRequiredService<Poyra.Modules.Field.Infrastructure.FieldOutcomeJob>()
            .ResolveAsync();
    }

    private async Task RunLinkOutcomeJobAsync()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();
        await scope.ServiceProvider
            .GetRequiredService<Poyra.Modules.PaymentLinks.Infrastructure.PaymentLinkOutcomeJob>()
            .ResolveAsync();
    }

    private async Task<List<CollectionDto>> CollectionsAsync(string apiKey)
        => await SendOk<List<CollectionDto>>(
            HttpMethod.Get, "/v1/field/collections", null, ("X-Api-Key", apiKey));

    /// <summary>Müşterinin bağlantıyı gerçekten ödemesi — checkout → banka formu → callback.</summary>
    private async Task<string> PayThroughCheckoutAsync(string slug)
    {
        var response = await _checkout.PostAsync($"/l/{slug}/ode",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var html = await response.Content.ReadAsStringAsync();
        var (url, fields) = ParseAutoSubmitForm(html);

        var callback = await _apiNoRedirect.PostAsync(url, new FormUrlEncodedContent(fields));
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await callback.Content.ReadAsStringAsync());

        var query = System.Web.HttpUtility.ParseQueryString(
            new Uri(callback.Headers.Location!.ToString(), UriKind.Absolute).Query);
        return query["poyra_payment_id"]!;
    }

    private static (string Url, Dictionary<string, string> Fields) ParseAutoSubmitForm(string html)
    {
        var action = System.Text.RegularExpressions.Regex.Match(
            html, """<form id="f" method="[^"]+" action="([^"]+)">""");
        action.Success.ShouldBeTrue("otomatik gönder formu bulunamadı");

        var fields = System.Text.RegularExpressions.Regex.Matches(
                html, """<input type="hidden" name="([^"]+)" value="([^"]*)" />""")
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value),
                          m => WebUtility.HtmlDecode(m.Groups[2].Value));

        return (WebUtility.HtmlDecode(action.Groups[1].Value), fields);
    }
}
