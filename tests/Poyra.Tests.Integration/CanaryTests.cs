using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Connectors.Infrastructure;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F1.4 canary: yoklama başarısız hesap Down'a çekilir, rota onu atlar —
/// kesinti işlem DENEMEDEN yakalanır; sağlam hesap Healthy kalır.
/// </summary>
[Collection("postgres")]
public sealed class CanaryTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _client;

    public CanaryTests(PostgresFixture fixture)
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
        });
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string ApiKey);
    private sealed record AccountDto(Guid Id, string Label, string Status, string Health);
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);

    private async Task<T> SendOk<T>(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        var response = await _client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task RunCanaryAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ConnectorCanaryJob>().ProbeAllAsync();
    }

    [Fact]
    public async Task Canary_bozuk_hesabi_down_yapmali_rota_islem_denemeden_atlamali()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Canary", slug = "canary-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));

        async Task<AccountDto> Add(string label, int priority, bool broken)
        {
            var credentials = new Dictionary<string, string> { ["secret"] = "s3cret" };
            if (broken)
                credentials["fail_initiate"] = "true";
            return await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts",
                new { connectorKey = "mockbank", label, credentials, priority },
                ("X-Api-Key", tenant.ApiKey));
        }

        var broken = await Add("Bozuk POS", priority: 1, broken: true);
        var healthy = await Add("Sağlam POS", priority: 2, broken: false);
        broken.Health.ShouldBe("healthy"); // henüz yoklanmadı

        await RunCanaryAsync();

        var accounts = await SendOk<List<AccountDto>>(HttpMethod.Get, "/v1/connector-accounts", null,
            ("X-Api-Key", tenant.ApiKey));
        accounts.Single(a => a.Id == broken.Id).Health.ShouldBe("down");
        accounts.Single(a => a.Id == healthy.Id).Health.ShouldBe("healthy");

        // Rota Down'u ATLAR: tek deneme, doğrudan sağlam hesapta — failover'a bile gerek kalmaz
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 5_000, confirm = true }, ("X-Api-Key", tenant.ApiKey));
        payment.Status.ShouldBe("requires_action");

        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        var attempts = await db.PaymentAttempts.AsNoTracking().ToListAsync();
        attempts.ShouldHaveSingleItem().ConnectorAccountId.ShouldBe(healthy.Id); // bozukta deneme YOK

        // Yoklama tekrarında sağlam hesap Healthy kalır (flap yok)
        await RunCanaryAsync();
        (await SendOk<List<AccountDto>>(HttpMethod.Get, "/v1/connector-accounts", null,
                ("X-Api-Key", tenant.ApiKey)))
            .Single(a => a.Id == healthy.Id).Health.ShouldBe("healthy");
    }
}
