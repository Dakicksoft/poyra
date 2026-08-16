using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F5.4: 3DS'li direct akış — kart BİZİM formumuzda toplanır (PCI kapsamı), kimlik
/// doğrulama bankada yapılır. Hosted akıştan farkı kartın nerede girildiğidir; sonuç
/// yine requires_action + banka formu + callback'tir.
///
/// Kritik güvenlik beklentisi: bankanın 3DS sayfasına giden formda KART VERİSİ OLMAMALI.
/// </summary>
[Collection("postgres")]
public sealed class ThreeDsDirectFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string TestPan = "4155650100416111"; // Luhn geçerli test kartı

    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _client;

    public ThreeDsDirectFlowTests(PostgresFixture fixture)
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
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string ApiKey);
    private sealed record AccountDto(Guid Id, string Label);
    private sealed record NextAction(string Type, string Url, string Method, Dictionary<string, string> Fields);
    private sealed record ErrorDto(string Code, string? RawCode, string? Message);
    private sealed record PaymentDto(
        string Id, string Status, long AmountMinor, long? ChargedAmountMinor,
        NextAction? NextAction, ErrorDto? LastError);
    private sealed record TimelineEvent(string EventType, string Actor);
    private sealed record Timeline(List<TimelineEvent> Events);
    private sealed record CatalogEntry(string Key, string DisplayName, string? Notes);

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        return await _client.SendAsync(request);
    }

    private async Task<T> SendOk<T>(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var response = await Send(method, path, body, headers);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<TenantCreated> SeedTenantAsync()
        => await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "3DS Direct A.Ş.", slug = "tds-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));

    private async Task<AccountDto> AddAccountAsync(
        string apiKey, string label, int priority, Dictionary<string, string>? extra = null)
    {
        var credentials = new Dictionary<string, string> { ["secret"] = "s3cret" };
        foreach (var (key, value) in extra ?? [])
            credentials[key] = value;

        return await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts",
            new { connectorKey = "mockbank", label, credentials, priority }, ("X-Api-Key", apiKey));
    }

    private async Task<PaymentDto> CreateAsync(string apiKey, long amountMinor, int installments = 1)
        => await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor, currency = "TRY", installments }, ("X-Api-Key", apiKey));

    private async Task<PaymentDto> ConfirmThreeDsAsync(string apiKey, string paymentId)
        => await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{paymentId}/confirm-direct", new
        {
            cardNumber = TestPan,
            expiryMonth = 12,
            expiryYear = 2030,
            cvv = "123",
            holderName = "AYSE YILMAZ",
            useThreeDs = true,
        }, ("X-Api-Key", apiKey));

    // ---- Senaryolar ----------------------------------------------------------

    [Fact]
    public async Task Uctan_uca_3ds_direct_tahsil_etmeli()
    {
        var tenant = await SeedTenantAsync();
        await AddAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);

        var created = await CreateAsync(tenant.ApiKey, 14_900);
        var confirmed = await ConfirmThreeDsAsync(tenant.ApiKey, created.Id);

        // 3DS'siz direct anında sonuçlanırdı; burada müşteri bankaya gider
        confirmed.Status.ShouldBe("requires_action");
        confirmed.NextAction.ShouldNotBeNull();

        // ★ Bankaya giden formda KART VERİSİ OLMAMALI — kart bizde kaldı, banka
        //   yalnız kendi ürettiği imzalı paketi görüyor (Posnet OOS ile aynı ilke)
        var payload = string.Join("|", confirmed.NextAction.Fields.Select(f => $"{f.Key}={f.Value}"));
        payload.ShouldNotContain(TestPan);
        payload.ShouldNotContain("123"); // CVV
        payload.ShouldNotContain("AYSE");

        // Banka dönüşü
        var callback = await _client.PostAsync(confirmed.NextAction.Url,
            new FormUrlEncodedContent(confirmed.NextAction.Fields));
        callback.StatusCode.ShouldBe(HttpStatusCode.OK, await callback.Content.ReadAsStringAsync());

        var fetched = await SendOk<PaymentDto>(HttpMethod.Get, $"/v1/payments/{created.Id}", null,
            ("X-Api-Key", tenant.ApiKey));
        fetched.Status.ShouldBe("succeeded");

        // Defterde akış türü ayırt edilebilir olmalı — denetim ve PCI kapsamı için
        var timeline = await SendOk<Timeline>(
            HttpMethod.Get, $"/v1/payments/{created.Id}/timeline", null, ("X-Api-Key", tenant.ApiKey));
        timeline.Events.ShouldContain(e => e.EventType == "attempt.initiated");
        timeline.Events.ShouldContain(e => e.EventType == "payment.succeeded");
    }

    [Fact]
    public async Task Uc_ds_dogrulamasi_basarisizsa_odeme_dusmeli()
    {
        var tenant = await SeedTenantAsync();
        await AddAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);

        // Mock kuralı: kuruş %100 == 98 → 3DS başarısız
        var created = await CreateAsync(tenant.ApiKey, 10_098);
        var confirmed = await ConfirmThreeDsAsync(tenant.ApiKey, created.Id);
        confirmed.Status.ShouldBe("requires_action");

        await _client.PostAsync(confirmed.NextAction!.Url,
            new FormUrlEncodedContent(confirmed.NextAction.Fields));

        var fetched = await SendOk<PaymentDto>(HttpMethod.Get, $"/v1/payments/{created.Id}", null,
            ("X-Api-Key", tenant.ApiKey));
        fetched.Status.ShouldBe("failed");
        fetched.LastError!.Code.ShouldBe("poyra.three_ds_failed");
    }

    [Fact]
    public async Task Erisilemeyen_pos_3ds_direct_te_de_failover_yapmali()
    {
        var tenant = await SeedTenantAsync();
        await AddAccountAsync(tenant.ApiKey, "Bozuk POS", priority: 1,
            new Dictionary<string, string> { ["fail_initiate"] = "true" });
        await AddAccountAsync(tenant.ApiKey, "Yedek POS", priority: 2);

        var created = await CreateAsync(tenant.ApiKey, 20_000);
        var confirmed = await ConfirmThreeDsAsync(tenant.ApiKey, created.Id);

        confirmed.Status.ShouldBe("requires_action"); // ikinci hesaptan devam etti

        var timeline = await SendOk<Timeline>(
            HttpMethod.Get, $"/v1/payments/{created.Id}/timeline", null, ("X-Api-Key", tenant.ApiKey));
        timeline.Events.Count(e => e.EventType == "attempt.failed").ShouldBe(1);
        timeline.Events.ShouldContain(e => e.EventType == "attempt.initiated");
    }

    [Fact]
    public async Task Desteklemeyen_konnektor_icin_anlasilir_hata_donmeli()
    {
        var tenant = await SeedTenantAsync();
        await AddAccountAsync(tenant.ApiKey, "3DS'siz POS", priority: 1,
            new Dictionary<string, string> { ["no_3ds_direct"] = "true" });

        var created = await CreateAsync(tenant.ApiKey, 10_000);
        var response = await Send(HttpMethod.Post, $"/v1/payments/{created.Id}/confirm-direct", new
        {
            cardNumber = TestPan,
            expiryMonth = 12,
            expiryYear = 2030,
            cvv = "123",
            useThreeDs = true,
        }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("direct_not_supported");
    }

    [Fact]
    public async Task Taksitli_3ds_direct_vade_farkli_tutari_bankaya_gondermeli()
    {
        var tenant = await SeedTenantAsync();
        var account = await AddAccountAsync(tenant.ApiKey, "Taksitli POS", priority: 1);

        await SendOk<object>(HttpMethod.Post, "/v1/installments/schemes",
            new { connectorAccountId = account.Id, program = "*", installmentCount = 3, customerRateBps = 1_000 },
            ("X-Api-Key", tenant.ApiKey));

        var created = await CreateAsync(tenant.ApiKey, 10_000, installments: 3);
        var confirmed = await ConfirmThreeDsAsync(tenant.ApiKey, created.Id);

        // Taksit köprüsü 3DS'li direct'te de geçerli: karttan vade farklı toplam çekilir
        confirmed.ChargedAmountMinor.ShouldBe(11_000);
        confirmed.NextAction!.Fields["mb_amount"].ShouldBe("11000");
    }

    [Fact]
    public async Task Ayni_odeme_iki_kez_confirm_edilememeli()
    {
        var tenant = await SeedTenantAsync();
        await AddAccountAsync(tenant.ApiKey, "Mock POS", priority: 1);

        var created = await CreateAsync(tenant.ApiKey, 12_500);
        await ConfirmThreeDsAsync(tenant.ApiKey, created.Id);

        // requires_action durumundaki ödeme yeniden başlatılamaz — çift çekim riski
        var second = await Send(HttpMethod.Post, $"/v1/payments/{created.Id}/confirm-direct", new
        {
            cardNumber = TestPan, expiryMonth = 12, expiryYear = 2030, cvv = "123", useThreeDs = true,
        }, ("X-Api-Key", tenant.ApiKey));
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Posnet_katalogda_pci_uyarisiyla_gorunmeli()
    {
        var tenant = await SeedTenantAsync();
        var catalog = await SendOk<List<CatalogEntry>>(
            HttpMethod.Get, "/v1/connectors/catalog", null, ("X-Api-Key", tenant.ApiKey));

        var posnet = catalog.SingleOrDefault(c => c.Key == "posnet");
        posnet.ShouldNotBeNull("Posnet katalogda olmalı");
        posnet.DisplayName.ShouldContain("Yapı Kredi");
        // İşyeri, hesabı açmadan önce PCI sonucunu görmeli
        posnet.Notes.ShouldContain("PCI");
    }

    [Fact]
    public async Task Posnet_hosted_akista_aday_olmamali()
    {
        var tenant = await SeedTenantAsync();

        // Posnet'te banka-hosted kart girişi YOKTUR; hesap eklenebilir ama hosted confirm
        // onu kullanamaz — deneme başarısız olur ve rota yedeğe geçer.
        await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "posnet",
            label = "YKB Posnet",
            credentials = new Dictionary<string, string>
            {
                ["gateway_base"] = "http://localhost:1",
                ["merchant_id"] = "6706598320",
                ["terminal_id"] = "67005551",
                ["pos_net_id"] = "27426",
                ["enc_key"] = "10,10,10,10,10,10,10,10",
            },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));
        await AddAccountAsync(tenant.ApiKey, "Mock POS", priority: 2);

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));

        // Posnet elendi, mock devraldı — akış kesilmedi
        payment.Status.ShouldBe("requires_action");
        payment.NextAction!.Fields.ShouldContainKey("mb_order");
    }
}
