using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Poyra.Modules.Payments.Infrastructure;
using Poyra.Modules.Webhooks.Infrastructure;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F1.2 uçtan uca: ödeme olayı → outbox (aynı transaction) → Hangfire → HMAC imzalı webhook
/// → GERÇEK bir HTTP alıcısına teslim. Alıcı, testin ayağa kaldırdığı gerçek Kestrel'dir
/// (API TestServer'da koşsa da giden HttpClient gerçek sokete çıkar).
/// </summary>
[Collection("postgres")]
public sealed class WebhookFlowTests : IAsyncLifetime
{
    private const string AdminKey = "test-admin-key";
    private readonly PostgresFixture _fixture;
    private WebApplicationFactory<ApiEntryPoint> _factory = null!;
    private HttpClient _client = null!;
    private WebhookSink _sink = null!;

    public WebhookFlowTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _sink = await WebhookSink.StartAsync();
        _factory = new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", _fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", _fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Platform:AdminKey", AdminKey);
            builder.UseSetting("Poyra:PublicBaseUrl", "http://localhost");
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _sink.DisposeAsync();
    }

    // ---- Gerçek HTTP alıcısı -------------------------------------------------
    private sealed class WebhookSink : IAsyncDisposable
    {
        private WebApplication _app = null!;
        public string HookUrl = null!;
        public volatile int RespondStatus = 200;
        public readonly ConcurrentQueue<(string Body, string Signature, string Event)> Received = new();

        public static async Task<WebhookSink> StartAsync()
        {
            var sink = new WebhookSink();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); // boş port
            sink._app = builder.Build();
            sink._app.MapPost("/hook", async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                sink.Received.Enqueue((
                    await reader.ReadToEndAsync(),
                    context.Request.Headers[WebhookSigner.Header].ToString(),
                    context.Request.Headers["Poyra-Event"].ToString()));
                context.Response.StatusCode = sink.RespondStatus;
            });
            await sink._app.StartAsync();
            sink.HookUrl = sink._app.Urls.First() + "/hook";
            return sink;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    // ---- Yardımcılar ---------------------------------------------------------
    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record EndpointCreated(Guid Id, string Url, string[] EventTypes, bool Active, string? Secret);
    private sealed record NextAction(string Type, string Url, string Method, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);
    private sealed record DeliveryDto(
        Guid Id, Guid EndpointId, string EventType, string Status, int AttemptCount,
        int? LastHttpStatus, string? LastError, DateTimeOffset? NextRetryAt, Guid? ReplayOfId);

    private Task<HttpResponseMessage> Send(
        HttpMethod method, string path, object? body, params (string Name, string Value)[] headers)
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

    private async Task<TenantCreated> CreateTenantWithMockAccountAsync()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Webhook Testi", slug = "wh-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));
        await SendOk<object>(HttpMethod.Post, "/v1/connector-accounts",
            new
            {
                connectorKey = "mockbank",
                label = "Mock POS",
                credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
                priority = 1,
            },
            ("X-Api-Key", tenant.ApiKey));
        return tenant;
    }

    private async Task<string> RunSuccessfulPaymentAsync(string apiKey, long amountMinor)
    {
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor, currency = "TRY", confirm = true }, ("X-Api-Key", apiKey));
        payment.Status.ShouldBe("requires_action");
        var callback = await _client.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields));
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);
        return payment.Id;
    }

    private static async Task<T?> PollAsync<T>(Func<Task<T?>> probe, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe() is { } hit)
                return hit;
            await Task.Delay(250);
        }

        return default;
    }

    // ---- Senaryolar ----------------------------------------------------------

    [Fact]
    public async Task Odeme_olayi_imzali_webhook_olarak_teslim_edilmeli()
    {
        var tenant = await CreateTenantWithMockAccountAsync();
        var endpoint = await SendOk<EndpointCreated>(HttpMethod.Post, "/v1/webhook-endpoints",
            new { url = _sink.HookUrl, eventTypes = new[] { "payment.succeeded" } },
            ("X-Api-Key", tenant.ApiKey));
        endpoint.Secret.ShouldNotBeNull();
        endpoint.Secret.ShouldStartWith("whsec_");

        var paymentId = await RunSuccessfulPaymentAsync(tenant.ApiKey, 14_900);

        var received = await PollAsync<(string Body, string Signature, string Event)?>(
            () => Task.FromResult(_sink.Received.TryDequeue(out var r) ? r : ((string, string, string)?)null),
            TimeSpan.FromSeconds(20));

        received.ShouldNotBeNull("webhook 20 sn içinde teslim edilmedi");
        received.Value.Event.ShouldBe("payment.succeeded");
        received.Value.Body.ShouldContain(paymentId);
        received.Value.Body.ShouldContain("14900");

        // İmza, alıcı tarafın doğrulayacağı şekilde geçerli olmalı
        WebhookSigner.Verify(endpoint.Secret, received.Value.Signature, received.Value.Body)
            .ShouldBeTrue("HMAC imzası doğrulanamadı");

        // Teslim günlüğü succeeded olmalı
        var deliveries = await SendOk<List<DeliveryDto>>(HttpMethod.Get,
            $"/v1/webhook-deliveries?endpointId={endpoint.Id}", null, ("X-Api-Key", tenant.ApiKey));
        deliveries.ShouldHaveSingleItem().Status.ShouldBe("succeeded");
        deliveries[0].AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task Basarisiz_teslim_yeniden_deneme_planlamali_replay_kurtarmali()
    {
        var tenant = await CreateTenantWithMockAccountAsync();
        var endpoint = await SendOk<EndpointCreated>(HttpMethod.Post, "/v1/webhook-endpoints",
            new { url = _sink.HookUrl, eventTypes = new[] { "payment.succeeded" } },
            ("X-Api-Key", tenant.ApiKey));

        _sink.RespondStatus = 500; // alıcı bozuk
        try
        {
            await RunSuccessfulPaymentAsync(tenant.ApiKey, 21_000);

            var failed = await PollAsync(async () =>
            {
                var list = await SendOk<List<DeliveryDto>>(HttpMethod.Get,
                    $"/v1/webhook-deliveries?endpointId={endpoint.Id}&status=failed", null,
                    ("X-Api-Key", tenant.ApiKey));
                return list.FirstOrDefault();
            }, TimeSpan.FromSeconds(20));

            failed.ShouldNotBeNull("başarısız teslim 20 sn içinde kayda geçmedi");
            failed.AttemptCount.ShouldBe(1);
            failed.LastHttpStatus.ShouldBe(500);
            failed.NextRetryAt.ShouldNotBeNull(); // yeniden deneme planlandı (1 dk sonrası)
        }
        finally
        {
            _sink.RespondStatus = 200; // alıcı düzeldi
        }

        while (_sink.Received.TryDequeue(out _)) { } // eski 500'lü çağrıları temizle

        // Replay: eski kayıt değişmez, yeni teslim açılır ve başarır
        var failedList = await SendOk<List<DeliveryDto>>(HttpMethod.Get,
            $"/v1/webhook-deliveries?endpointId={endpoint.Id}&status=failed", null,
            ("X-Api-Key", tenant.ApiKey));
        var replay = await SendOk<DeliveryDto>(HttpMethod.Post,
            $"/v1/webhook-deliveries/{failedList[0].Id}/replay", null, ("X-Api-Key", tenant.ApiKey));
        replay.ReplayOfId.ShouldBe(failedList[0].Id);

        var delivered = await PollAsync(async () =>
        {
            var list = await SendOk<List<DeliveryDto>>(HttpMethod.Get,
                $"/v1/webhook-deliveries?endpointId={endpoint.Id}&status=succeeded", null,
                ("X-Api-Key", tenant.ApiKey));
            return list.FirstOrDefault(d => d.Id == replay.Id);
        }, TimeSpan.FromSeconds(20));

        delivered.ShouldNotBeNull("replay teslimi 20 sn içinde başarmadı");
    }

    [Fact]
    public async Task Abone_olunmayan_olay_teslim_edilmemeli()
    {
        var tenant = await CreateTenantWithMockAccountAsync();
        var endpoint = await SendOk<EndpointCreated>(HttpMethod.Post, "/v1/webhook-endpoints",
            new { url = _sink.HookUrl, eventTypes = new[] { "refund.succeeded" } }, // yalnız iade
            ("X-Api-Key", tenant.ApiKey));

        var paymentId = await RunSuccessfulPaymentAsync(tenant.ApiKey, 30_000);

        // İade olayını tetikle → bu TESLİM EDİLMELİ (pozitif sinyal, beklemeyi sonlandırır)
        await SendOk<object>(HttpMethod.Post, "/v1/refunds",
            new { paymentId }, ("X-Api-Key", tenant.ApiKey));

        var received = await PollAsync<(string Body, string Signature, string Event)?>(
            () => Task.FromResult(_sink.Received.TryDequeue(out var r) ? r : ((string, string, string)?)null),
            TimeSpan.FromSeconds(20));

        received.ShouldNotBeNull();
        received.Value.Event.ShouldBe("refund.succeeded"); // payment.succeeded ASLA gelmedi

        var deliveries = await SendOk<List<DeliveryDto>>(HttpMethod.Get,
            $"/v1/webhook-deliveries?endpointId={endpoint.Id}", null, ("X-Api-Key", tenant.ApiKey));
        deliveries.ShouldHaveSingleItem().EventType.ShouldBe("refund.succeeded");
    }

    [Fact]
    public async Task Sahipsiz_3ds_oturumu_zaman_asimiyla_failed_olmali()
    {
        var tenant = await CreateTenantWithMockAccountAsync();

        // confirm edilir ama banka formu ASLA post edilmez (müşteri kayboldu)
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 42_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        payment.Status.ShouldBe("requires_action");

        // Belirteci yapay olarak eskit (sahip rol bağlantısıyla — test düzeneği)
        await using (var conn = new NpgsqlConnection(_fixture.OwnerCs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                """
                UPDATE callback_tokens SET expires_at = now() - interval '30 minutes'
                WHERE tenant_id = @tenant AND used_at IS NULL
                """, conn);
            cmd.Parameters.AddWithValue("tenant", tenant.TenantId);
            (await cmd.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        // İşi deterministik koş (recurring beklemek yerine)
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ThreeDsTimeoutJob>().ExpireStaleAsync();
        }

        var fetched = await SendOk<PaymentDto>(HttpMethod.Get, $"/v1/payments/{payment.Id}", null,
            ("X-Api-Key", tenant.ApiKey));
        fetched.Status.ShouldBe("failed");

        // Çok geç gelen banka dönüşü durumu DEĞİŞTİRMEMELİ: belirteç damgalandığı için
        // idempotent yol devreye girer, mevcut (failed) durum döner — succeeded olamaz.
        var lateCallback = await _client.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields));
        lateCallback.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await lateCallback.Content.ReadAsStringAsync()).ShouldContain("failed");

        var after = await SendOk<PaymentDto>(HttpMethod.Get, $"/v1/payments/{payment.Id}", null,
            ("X-Api-Key", tenant.ApiKey));
        after.Status.ShouldBe("failed"); // durum makinesi tutarlı kaldı
    }
}
