using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F7 pekiştirme: <c>Idempotency-Key</c>. Ağ zaman aşımı bir cevapsızlıktır — istek
/// bankaya ulaşmış ve para çekilmiş olabilir. Tekrar denemek YENİ İŞLEM açarsa müşteri
/// iki kez öder ve bunu ancak ekstresinde fark eder.
/// </summary>
[Collection("postgres")]
public sealed class IdempotencyTests : IDisposable
{
    private const string AdminKey = "test-admin-key";

    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _api;

    public IdempotencyTests(PostgresFixture fixture)
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

    private sealed record TenantCreated(Guid TenantId, string ApiKey);
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, long AmountMinor, NextAction? NextAction);
    private sealed record RefundDto(string Id, long AmountMinor, string Status);

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body,
        string? idempotencyKey = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        return await _api.SendAsync(request);
    }

    private async Task<T> SendOk<T>(HttpMethod method, string path, object? body,
        string? idempotencyKey = null, params (string Name, string Value)[] headers)
    {
        var response = await Send(method, path, body, idempotencyKey, headers);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<TenantCreated> SeedAsync()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Idem A.Ş.", slug = "idm-" + Guid.NewGuid().ToString("N")[..10] },
            null, ("X-Platform-Key", AdminKey));

        await SendOk<object>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, null, ("X-Api-Key", tenant.ApiKey));

        return tenant;
    }

    // ---- Temel davranış ---------------------------------------------------------

    [Fact]
    public async Task Ayni_anahtarla_tekrar_YENI_odeme_acmamali()
    {
        var tenant = await SeedAsync();
        var key = Guid.NewGuid().ToString();

        var first = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 50_000, currency = "TRY" }, key, ("X-Api-Key", tenant.ApiKey));

        var second = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 50_000, currency = "TRY" }, key, ("X-Api-Key", tenant.ApiKey));

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.Contains("Idempotency-Replayed").ShouldBeTrue();

        var replayed = (await second.Content.ReadFromJsonAsync<PaymentDto>())!;
        replayed.Id.ShouldBe(first.Id); // ★ aynı kayıt, ikinci intent DEĞİL

        // Defterde tek bir intent var
        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        (await db.PaymentIntents.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Anahtarsiz_istek_korunmamali_ve_eskisi_gibi_calismali()
    {
        var tenant = await SeedAsync();

        // Geriye uyum: başlık göndermeyen mevcut entegrasyonlar bozulmamalı
        var first = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, currency = "TRY" }, null, ("X-Api-Key", tenant.ApiKey));
        var second = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, currency = "TRY" }, null, ("X-Api-Key", tenant.ApiKey));

        second.Id.ShouldNotBe(first.Id);

        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        (await db.PaymentIntents.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Farkli_anahtar_farkli_odeme_acmali()
    {
        var tenant = await SeedAsync();

        var first = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, currency = "TRY" }, Guid.NewGuid().ToString(),
            ("X-Api-Key", tenant.ApiKey));
        var second = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, currency = "TRY" }, Guid.NewGuid().ToString(),
            ("X-Api-Key", tenant.ApiKey));

        second.Id.ShouldNotBe(first.Id);
    }

    // ---- Yanlış kullanım --------------------------------------------------------

    [Fact]
    public async Task Ayni_anahtarla_FARKLI_istek_reddedilmeli()
    {
        var tenant = await SeedAsync();
        var key = Guid.NewGuid().ToString();

        await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 50_000, currency = "TRY" }, key, ("X-Api-Key", tenant.ApiKey));

        // İşyeri anahtarı yeniden kullandı ama tutar değişti. Sessizce ilk sonucu
        // döndürmek, ikinci ödemenin HİÇ YAPILMADIĞINI gizlerdi.
        var reused = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 75_000, currency = "TRY" }, key, ("X-Api-Key", tenant.ApiKey));

        reused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await reused.Content.ReadAsStringAsync()).ShouldContain("idempotency.key_reused");

        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        (await db.PaymentIntents.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Cok_uzun_anahtar_reddedilmeli()
    {
        var tenant = await SeedAsync();

        var response = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 1_000, currency = "TRY" }, new string('x', 256),
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("idempotency.key_too_long");
    }

    // ---- Hatalar --------------------------------------------------------------

    [Fact]
    public async Task Dogrulama_hatasi_anahtari_yakmamali()
    {
        var tenant = await SeedAsync();
        var key = Guid.NewGuid().ToString();

        var rejected = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = -5, currency = "TRY" }, key, ("X-Api-Key", tenant.ApiKey));
        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Doğrulamada düşen istek hiçbir şey YAPMADI. Anahtarı kilitlemek, işyerini
        // hatasını düzelttikten sonra "bu anahtar kullanılmış" duvarına çarptırırdı.
        var fixedRequest = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 5_000, currency = "TRY" }, key, ("X-Api-Key", tenant.ApiKey));

        fixedRequest.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixedRequest.Headers.Contains("Idempotency-Replayed").ShouldBeFalse();
    }

    [Fact]
    public async Task Is_kurali_reddi_de_anahtari_yakmamali()
    {
        var tenant = await SeedAsync();
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 40_000, currency = "TRY" }, null, ("X-Api-Key", tenant.ApiKey));

        var key = Guid.NewGuid().ToString();

        // Henüz tahsil edilmemiş ödemeye iade: iş kuralı reddi (PoyraException)
        var rejected = await Send(HttpMethod.Post, "/v1/refunds",
            new { paymentId = payment.Id, amountMinor = 10_000 }, key, ("X-Api-Key", tenant.ApiKey));
        rejected.StatusCode.ShouldBeOneOf(HttpStatusCode.Conflict, HttpStatusCode.BadRequest);
        (await rejected.Content.ReadAsStringAsync()).ShouldNotContain("idempotency.");

        // Ödeme tamamlandıktan sonra AYNI anahtarla iade geçmeli
        var confirmed = await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{payment.Id}/confirm",
            new { }, null, ("X-Api-Key", tenant.ApiKey));
        (await _api.PostAsync(confirmed.NextAction!.Url,
            new FormUrlEncodedContent(confirmed.NextAction.Fields))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var accepted = await Send(HttpMethod.Post, "/v1/refunds",
            new { paymentId = payment.Id, amountMinor = 10_000 }, key, ("X-Api-Key", tenant.ApiKey));
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- İşyeri yalıtımı --------------------------------------------------------

    [Fact]
    public async Task Anahtar_ISYERINE_ozel_olmali()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        var key = "siparis-1024"; // iki işyeri de kendi sipariş numarasını kullanıyor

        var a = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 30_000, currency = "TRY" }, key, ("X-Api-Key", first.ApiKey));

        // İkinci işyerinin aynı anahtarı, birincininkini görmemeli — yoksa iki
        // işyeri birbirinin ödemesini "tekrar" sanardı
        var b = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 44_000, currency = "TRY" }, key, ("X-Api-Key", second.ApiKey));

        b.Id.ShouldNotBe(a.Id);
        b.AmountMinor.ShouldBe(44_000);
    }

    // ---- İade -------------------------------------------------------------------

    [Fact]
    public async Task Tekrarlanan_iade_ikinci_kez_para_iade_ETMEMELI()
    {
        var tenant = await SeedAsync();

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 60_000, currency = "TRY", confirm = true }, null,
            ("X-Api-Key", tenant.ApiKey));
        (await _api.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var key = Guid.NewGuid().ToString();
        var first = await SendOk<RefundDto>(HttpMethod.Post, "/v1/refunds",
            new { paymentId = payment.Id, amountMinor = 20_000 }, key, ("X-Api-Key", tenant.ApiKey));

        var second = await Send(HttpMethod.Post, "/v1/refunds",
            new { paymentId = payment.Id, amountMinor = 20_000 }, key, ("X-Api-Key", tenant.ApiKey));

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<RefundDto>())!.Id.ShouldBe(first.Id);

        // ★ Defterde TEK iade: 400 ₺ değil 200 ₺ geri gitti
        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        var refunds = await db.Refunds.AsNoTracking().ToListAsync();
        refunds.Count.ShouldBe(1);
        refunds[0].AmountMinor.ShouldBe(20_000);
    }

    // ---- Kapsam ----------------------------------------------------------------

    [Fact]
    public async Task Yapilandirma_uclari_korunmamali()
    {
        var tenant = await SeedAsync();
        var key = Guid.NewGuid().ToString();

        // Kapsam dar tutuldu: her isteğe yazma eklemek bedava değil. POS eklemek
        // para hareketi değildir ve iki kez eklenirse panelde görülür.
        await SendOk<object>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "İkinci POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 2,
        }, key, ("X-Api-Key", tenant.ApiKey));

        var second = await Send(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Üçüncü POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 3,
        }, key, ("X-Api-Key", tenant.ApiKey));

        second.StatusCode.ShouldBe(HttpStatusCode.OK); // anahtar yok sayıldı
    }

    [Fact]
    public async Task Onaylama_da_korunmali()
    {
        var tenant = await SeedAsync();
        var intent = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 35_000, currency = "TRY" }, null, ("X-Api-Key", tenant.ApiKey));

        var key = Guid.NewGuid().ToString();
        var first = await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{intent.Id}/confirm",
            new { }, key, ("X-Api-Key", tenant.ApiKey));

        var second = await Send(HttpMethod.Post, $"/v1/payments/{intent.Id}/confirm",
            new { }, key, ("X-Api-Key", tenant.ApiKey));

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.Contains("Idempotency-Replayed").ShouldBeTrue();

        // Tek deneme (attempt) açıldı — ikinci confirm bankaya İKİNCİ kez gitmedi
        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        (await db.PaymentAttempts.CountAsync()).ShouldBe(1);
        first.Status.ShouldBe("requires_action");
    }

    // ---- Eşzamanlılık -----------------------------------------------------------

    [Fact]
    public async Task Es_zamanli_iki_istek_tek_odeme_uretmeli()
    {
        var tenant = await SeedAsync();
        var key = Guid.NewGuid().ToString();

        // İşyerinin iki sunucusu aynı anda tetiklendi (kuyruk çift teslim etti)
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            Send(HttpMethod.Post, "/v1/payments",
                new { amountMinor = 25_000, currency = "TRY" }, key, ("X-Api-Key", tenant.ApiKey))));

        // Biri kazanır; diğeri ya tekrarı okur (200) ya da "hâlâ işleniyor" der (409).
        // İkisi de kabul edilebilir — kabul EDİLEMEZ olan iki ödeme açılmasıdır.
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBeGreaterThanOrEqualTo(1);
        foreach (var response in responses)
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);

        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        (await db.PaymentIntents.CountAsync()).ShouldBe(1);
    }
}
