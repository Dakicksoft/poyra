using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F6.5/F6.6 e-ihracat konnektörleri (Stripe/Adyen) uçtan uca. Bu sağlayıcılar
/// TR bankalarından iki noktada ayrılır ve ikisi de akışı etkiler:
///  ① Müşteri imzalı bir forma POST edilmez, hazır bir adrese YÖNLENDİRİLİR.
///  ② Türkiye banka taksidi yoktur — taksitli işlem bu hesaplara gitmemelidir.
/// </summary>
[Collection("postgres")]
public sealed class ExportConnectorFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";

    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _api;

    public ExportConnectorFlowTests(PostgresFixture fixture)
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
        _api = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string ApiKey);
    private sealed record AccountDto(Guid Id, string Label);
    private sealed record NextAction(string Type, string Url, string Method, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);
    private sealed record CatalogEntry(
        string Key, string DisplayName, string Type, bool SupportsInstallments,
        bool SupportsVoid, bool SupportsRefund);

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
            new { name = "İhracat A.Ş.", slug = "ihr-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));

    // ---- Katalog ----------------------------------------------------------------

    [Fact]
    public async Task Katalog_ihracat_konnektorlerini_dogru_beyan_etmeli()
    {
        var tenant = await SeedTenantAsync();
        var catalog = await SendOk<List<CatalogEntry>>(
            HttpMethod.Get, "/v1/connectors/catalog", null, ("X-Api-Key", tenant.ApiKey));

        foreach (var key in new[] { "stripe", "adyen" })
        {
            var entry = catalog.SingleOrDefault(c => c.Key == key);
            entry.ShouldNotBeNull($"{key} katalogda olmalı");
            entry.Type.ShouldBe("paymentinstitution");
            entry.SupportsRefund.ShouldBeTrue();
            // Yanlış beyan, taksitli işlemin bu hesaba yönlenip sessizce tek çekim
            // olarak alınmasına yol açardı
            entry.SupportsInstallments.ShouldBeFalse();
        }
    }

    // ---- Yönlendirme akışı -------------------------------------------------------

    [Fact]
    public async Task Erisilemeyen_ihracat_hesabi_failover_tetiklemeli()
    {
        var tenant = await SeedTenantAsync();

        // Ulaşılamayan adres: sağlayıcı erişilemez sayılmalı, kart reddi DEĞİL
        await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "adyen",
            label = "Adyen (kapalı)",
            credentials = new Dictionary<string, string>
            {
                ["gateway_base"] = "http://127.0.0.1:1",
                ["api_key"] = "sahte",
                ["merchant_account"] = "PoyraECOM",
            },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Yedek POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 2,
        }, ("X-Api-Key", tenant.ApiKey));

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 20_000, currency = "EUR", confirm = true }, ("X-Api-Key", tenant.ApiKey));

        // Adyen düştü, yedek devraldı — akış kesilmedi
        payment.Status.ShouldBe("requires_action");
        payment.NextAction!.Fields.ShouldContainKey("mb_order");
        payment.NextAction.Type.ShouldBe("redirect_form"); // TR bankası: imzalı form POST'u
    }

    [Fact]
    public async Task Taksitli_islem_ihracat_hesabina_gitmemeli()
    {
        var tenant = await SeedTenantAsync();
        var stripe = await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "stripe",
            label = "Stripe",
            credentials = new Dictionary<string, string> { ["secret_key"] = "sk_test_sahte" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        // İşyeri YANLIŞLIKLA Stripe hesabına taksit şeması tanımlıyor.
        // Şema yokluğuna güvenmek yetmez: konnektörün BEYANI kazanmalı, yoksa
        // taksitli işlem Stripe'a gider ve tutar sessizce TEK ÇEKİM olarak alınır.
        await SendOk<object>(HttpMethod.Post, "/v1/installments/schemes",
            new { connectorAccountId = stripe.Id, program = "*", installmentCount = 3, customerRateBps = 0 },
            ("X-Api-Key", tenant.ApiKey));

        var created = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 30_000, currency = "EUR", installments = 3 }, ("X-Api-Key", tenant.ApiKey));

        var confirm = await Send(HttpMethod.Post, $"/v1/payments/{created.Id}/confirm",
            new { }, ("X-Api-Key", tenant.ApiKey));

        confirm.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await confirm.Content.ReadAsStringAsync()).ShouldContain("installments.not_offered");

        // Tek çekim aynı hesapta denenebilir (adres sahte olduğu için erişilemez döner,
        // ama TAKSİT engeli yüzünden değil)
        var single = await Send(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 30_000, currency = "EUR", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        (await single.Content.ReadAsStringAsync()).ShouldNotContain("installments.not_offered");
    }

    // ---- GET yönlendirmesi uçtan uca ---------------------------------------------

    [Fact]
    public async Task Saglayici_yonlendirmesi_forma_cevrilmemeli()
    {
        var tenant = await SeedTenantAsync();

        // Sahte Stripe: oturum açar ve SORGU DİZELİ bir adres döner
        await using var stripe = new FakeProvider();
        await stripe.StartAsync();
        stripe.Respond("/v1/checkout/sessions", HttpStatusCode.OK,
            """{"id":"cs_1","url":"REPLACE/pay?x=1&y=2"}""".Replace("REPLACE", stripe.BaseUrl));

        await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "adyen", // Adyen gateway adresi kimlik alanı — sahteye yönlendirilebilir
            label = "Sahte sağlayıcı",
            credentials = new Dictionary<string, string>
            {
                ["gateway_base"] = stripe.BaseUrl,
                ["api_key"] = "sahte",
                ["merchant_account"] = "PoyraECOM",
            },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        stripe.Respond("/v71/paymentLinks", HttpStatusCode.Created,
            $$"""{"id":"PL9","url":"{{stripe.BaseUrl}}/pay?x=1&y=2"}""");

        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 45_000, currency = "EUR", confirm = true }, ("X-Api-Key", tenant.ApiKey));

        payment.Status.ShouldBe("requires_action");
        var action = payment.NextAction.ShouldNotBeNull();

        // ★ Sorgu dizesi korunmalı: GET adresini form POST'una çevirmek onu düşürür
        //   ve müşteri sağlayıcının hata sayfasına düşerdi
        action.Type.ShouldBe("redirect");
        action.Method.ShouldBe("GET");
        action.Fields.ShouldBeEmpty();
        action.Url.ShouldEndWith("/pay?x=1&y=2");

        // Konnektör durumu (Adyen bağlantı kimliği) müşteriye SIZMAMALI
        System.Text.Json.JsonSerializer.Serialize(action).ShouldNotContain("PL9");
    }

    // ---- Sahte sağlayıcı ---------------------------------------------------------

    private sealed class FakeProvider : IAsyncDisposable
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = [];
        private HttpListener _listener = null!;
        public string BaseUrl { get; private set; } = "";

        public void Respond(string path, HttpStatusCode status, string body)
            => _responses[path] = (status, body);

        public Task StartAsync()
        {
            var port = Random.Shared.Next(50000, 60000);
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();

            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch
                    {
                        return;
                    }

                    var (status, body) = _responses.GetValueOrDefault(
                        context.Request.Url!.AbsolutePath, (HttpStatusCode.NotFound, "{}"));

                    context.Response.StatusCode = (int)status;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
                    context.Response.Close();
                }
            });

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }
}
