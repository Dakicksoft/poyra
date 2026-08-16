using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Api;
using Poyra.Modules.Tenancy.Domain;
using Poyra.SharedKernel.Tenancy;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// M18 SMS. Türkiye'de ödeme bağlantısının asıl kullanım biçimi budur: işyeri
/// müşteriyi arar, bağlantıyı SMS'le atar. Testler iki sessiz arızayı hedefler —
/// yanlış biçimli numaraya "gönderildi" demek ve Türkçe karakterli mesajın
/// krediyi üçe katlaması.
/// </summary>
[Collection("postgres")]
public sealed class SmsFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";

    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _api;

    public SmsFlowTests(PostgresFixture fixture)
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
            builder.UseSetting("Poyra:CheckoutBaseUrl", "http://localhost:5095");
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Poyra:JwtKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            builder.UseSetting("Poyra:VaultKey", Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
        });
        _api = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string ApiKey);
    private sealed record LinkDto(string Id, string Slug, string Url, long? AmountMinor);
    private sealed record SmsResultDto(bool Queued, string Phone, int Segments, string Body);

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
        => await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "SMS A.Ş.", slug = "sms-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));

    private Task<LinkDto> CreateLinkAsync(string apiKey, long? amountMinor = 14_990)
        => SendOk<LinkDto>(HttpMethod.Post, "/v1/payment-links",
            new { description = "Danışmanlık bedeli", amountMinor, currency = "TRY" },
            ("X-Api-Key", apiKey));

    private async Task<List<SmsMessageRecord>> OutboxAsync()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();
        var db = scope.ServiceProvider.GetRequiredService<Poyra.Modules.Tenancy.TenancyDbContext>();
        return await db.SmsMessages.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync();
    }

    // ---- Gönderim ------------------------------------------------------------------

    [Fact]
    public async Task Baglanti_SMS_ile_gonderilebilmeli()
    {
        var tenant = await SeedTenantAsync();
        var link = await CreateLinkAsync(tenant.ApiKey);
        var before = (await OutboxAsync()).Count;

        var result = await SendOk<SmsResultDto>(HttpMethod.Post, $"/v1/payment-links/{link.Id}/sms",
            new { phone = "0532 123 45 67" }, ("X-Api-Key", tenant.ApiKey));

        result.Queued.ShouldBeTrue();
        result.Phone.ShouldBe("+905321234567"); // her yazım aynı biçime normalleşir
        result.Body.ShouldContain(link.Url);
        result.Body.ShouldContain("149,90"); // tutar yazılır: ne ödeyeceğini bilmeden tıklamak güven sorunudur

        // ★ İşyeri adı "SMS A.Ş." — tek bir 'Ş' mesajı UCS-2'ye düşürür ve krediyi
        //   ikiye katlardı. Varsayılan metin ASCII'ye indirgenir.
        result.Body.ShouldContain("SMS A.S.");
        result.Body.ShouldNotContain("Ş");
        result.Segments.ShouldBe(1);

        var outbox = await OutboxAsync();
        outbox.Count.ShouldBe(before + 1);
        var queued = outbox[^1];
        queued.ToPhone.ShouldBe("+905321234567");
        queued.Status.ShouldBe(EmailStatus.Pending); // teslimat ayrı iştir
        queued.Purpose.ShouldBe("payment_link");
    }

    [Fact]
    public async Task Sabit_hat_reddedilmeli()
    {
        var tenant = await SeedTenantAsync();
        var link = await CreateLinkAsync(tenant.ApiKey);
        var before = (await OutboxAsync()).Count;

        // SMS gitmeyecek bir numaraya "gönderildi" demek, işyerinin müşteriye
        // ulaştığını sanması demektir
        var response = await Send(HttpMethod.Post, $"/v1/payment-links/{link.Id}/sms",
            new { phone = "0212 123 45 67" }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OutboxAsync()).Count.ShouldBe(before); // kuyruğa hiçbir şey girmedi
    }

    [Fact]
    public async Task Kapali_baglanti_gonderilememeli()
    {
        var tenant = await SeedTenantAsync();
        var link = await CreateLinkAsync(tenant.ApiKey);
        await SendOk<object>(HttpMethod.Post, $"/v1/payment-links/{link.Id}/disable", null,
            ("X-Api-Key", tenant.ApiKey));

        // Müşteri tıklayınca hata sayfası görecekse SMS göndermek kredi israfıdır
        var response = await Send(HttpMethod.Post, $"/v1/payment-links/{link.Id}/sms",
            new { phone = "05321234567" }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("payment_link.disabled");
    }

    [Fact]
    public async Task Acik_tutarli_baglantida_tutar_yazilmamali()
    {
        var tenant = await SeedTenantAsync();
        var link = await CreateLinkAsync(tenant.ApiKey, amountMinor: null);

        var result = await SendOk<SmsResultDto>(HttpMethod.Post, $"/v1/payment-links/{link.Id}/sms",
            new { phone = "05321234567" }, ("X-Api-Key", tenant.ApiKey));

        // Tutarı müşteri girecek — mesajda uydurma bir rakam olmamalı
        result.Body.ShouldNotContain("TL");
        result.Body.ShouldContain(link.Url);
    }

    [Fact]
    public async Task Ozel_metin_kullanilabilmeli()
    {
        var tenant = await SeedTenantAsync();
        var link = await CreateLinkAsync(tenant.ApiKey);

        var result = await SendOk<SmsResultDto>(HttpMethod.Post, $"/v1/payment-links/{link.Id}/sms",
            new { phone = "05321234567", message = "Randevu ucretiniz icin:" },
            ("X-Api-Key", tenant.ApiKey));

        result.Body.ShouldStartWith("Randevu ucretiniz icin:");
        result.Body.ShouldContain(link.Url);
    }

    [Fact]
    public async Task Turkce_karakter_kredi_sayisini_yanitta_gostermeli()
    {
        var tenant = await SeedTenantAsync();
        var link = await CreateLinkAsync(tenant.ApiKey);

        // 100 karakterlik Türkçe metin tek kredi SANILIR ama UCS-2'de 70 sınırı vardır
        var turkish = new string('ş', 100);
        var result = await SendOk<SmsResultDto>(HttpMethod.Post, $"/v1/payment-links/{link.Id}/sms",
            new { phone = "05321234567", message = turkish }, ("X-Api-Key", tenant.ApiKey));

        result.Segments.ShouldBeGreaterThan(1);

        var outbox = await OutboxAsync();
        outbox[^1].Segments.ShouldBe(result.Segments); // fatura bu sayıyla karşılaştırılır
    }

    // ---- Teslimat işçisi -------------------------------------------------------------

    [Fact]
    public async Task Teslimat_isi_kuyrugu_bosaltmali()
    {
        var tenant = await SeedTenantAsync();
        var link = await CreateLinkAsync(tenant.ApiKey);
        await SendOk<SmsResultDto>(HttpMethod.Post, $"/v1/payment-links/{link.Id}/sms",
            new { phone = "05329998877" }, ("X-Api-Key", tenant.ApiKey));

        // Test ortamında sağlayıcı tanımlı değil → günlüğe yazan taşıyıcı seçilir
        // ve mesaj 'sent' damgalanır. Sessizce 'pending' kalması, kuyruğun
        // tıkandığını gizlerdi.
        for (var round = 0; round < 5; round++)
        {
            using var scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();
            await scope.ServiceProvider
                .GetRequiredService<Poyra.Modules.Tenancy.Infrastructure.SmsDispatchJob>()
                .DispatchPendingAsync();

            var mine = (await OutboxAsync()).SingleOrDefault(m => m.ToPhone == "+905329998877");
            if (mine?.Status == EmailStatus.Sent)
            {
                mine.SentAt.ShouldNotBeNull();
                mine.AttemptCount.ShouldBe(1);
                return;
            }
        }

        throw new InvalidOperationException("SMS 5 turda gönderilemedi.");
    }

    // ---- Defter -----------------------------------------------------------------------

    [Fact]
    public async Task Gonderilen_SMS_silinemez_olmali()
    {
        var tenant = await SeedTenantAsync();
        var link = await CreateLinkAsync(tenant.ApiKey);
        await SendOk<SmsResultDto>(HttpMethod.Post, $"/v1/payment-links/{link.Id}/sms",
            new { phone = "05327776655" }, ("X-Api-Key", tenant.ApiKey));

        await using var db = _fixture.CreateTenancy(TenantContext.Platform);
        var record = await db.SmsMessages.SingleAsync(m => m.ToPhone == "+905327776655");

        // "Bu bağlantı gerçekten gönderildi mi" bir denetim sorusudur ve kredi
        // faturası bu kayıtlarla karşılaştırılır
        db.SmsMessages.Remove(record);
        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        error.InnerException.ShouldBeOfType<Npgsql.PostgresException>().SqlState.ShouldBe("42501");
    }
}
