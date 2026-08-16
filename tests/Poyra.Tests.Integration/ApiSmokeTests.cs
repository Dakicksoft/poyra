using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// Uçtan uca duman testi: gerçek HTTP boru hattı (middleware → FastEndpoints → dispatcher
/// → EF → RLS'li Postgres). İşyeri oluştur → anahtarıyla ödeme aç → oku → izolasyonu doğrula.
/// </summary>
[Collection("postgres")]
public sealed class ApiSmokeTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _client;

    public ApiSmokeTests(PostgresFixture fixture)
    {
        _factory = new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing"); // Development değil → X-Tenant-Id dev kapısı kapalı
            builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false"); // fikstür zaten migrate etti
            builder.UseSetting("Platform:AdminKey", AdminKey);
        });
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, Guid OrganizationId, Guid ProfileId, string Slug, string ApiKey);
    private sealed record PaymentDto(string Id, string Status, long AmountMinor, string Currency, string? Description, string ClientSecret, DateTimeOffset CreatedAt);

    [Fact]
    public async Task Uctan_uca_isyeri_ve_odeme_akisi()
    {
        var slug = "smoke-" + Guid.NewGuid().ToString("N")[..10];

        // 1) Platform anahtarı olmadan işyeri oluşturulamaz
        var denied = await _client.PostAsJsonAsync("/v1/tenants", new { name = "Duman Testi", slug });
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // 2) Platform anahtarıyla işyeri + tek seferlik API anahtarı
        var created = await SendAsync<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Duman Testi", slug },
            ("X-Platform-Key", AdminKey));
        created.ApiKey.ShouldStartWith("sk_test_");
        created.Slug.ShouldBe(slug);

        // 3) Anahtarsız ödeme → 401
        var noKey = await _client.PostAsJsonAsync("/v1/payments", new { amountMinor = 12_345 });
        noKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // 4) Anahtarla ödeme oluştur
        var payment = await SendAsync<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 12_345, currency = "TRY", description = "Duman ödemesi" },
            ("X-Api-Key", created.ApiKey));
        payment.Id.ShouldStartWith("pay_");
        payment.Status.ShouldBe("created"); // snake_case enum serileştirme
        payment.AmountMinor.ShouldBe(12_345);
        payment.ClientSecret.ShouldStartWith("psec_");

        // 5) Kendi anahtarıyla okunur
        var fetched = await SendAsync<PaymentDto>(HttpMethod.Get, $"/v1/payments/{payment.Id}",
            body: null, ("X-Api-Key", created.ApiKey));
        fetched.Id.ShouldBe(payment.Id);

        // 6) Başka işyerinin anahtarıyla görünmez (Katman A + B)
        var other = await SendAsync<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Öteki", slug = "diger-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));
        var crossTenant = await SendRawAsync(HttpMethod.Get, $"/v1/payments/{payment.Id}",
            body: null, ("X-Api-Key", other.ApiKey));
        crossTenant.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // 7) Doğrulama hatası tek tip sözlükle döner
        var invalid = await SendRawAsync(HttpMethod.Post, "/v1/payments",
            new { amountMinor = -5 }, ("X-Api-Key", created.ApiKey));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync()).ShouldContain("validation_failed");
    }

    [Fact]
    public async Task Saglik_uclari_calismali()
    {
        (await _client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, params (string Name, string Value)[] headers)
    {
        var response = await SendRawAsync(method, path, body, headers);
        response.IsSuccessStatusCode.ShouldBeTrue($"{method} {path} → {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        return _client.SendAsync(request);
    }
}
