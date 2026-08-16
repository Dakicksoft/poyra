using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F1.4 quote→confirm köprüsü: taksitli confirm'de vade farklı toplam KARTTAN çekilir
/// (mal bedeli intent'te kalır), iade tavanı çekilen tutardır, taksidi sunmayan hesap
/// rota tarafından atlanır — hiçbiri sunmuyorsa 409 (intent Created kalır).
/// </summary>
[Collection("postgres")]
public sealed class InstallmentBridgeTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _client;

    public InstallmentBridgeTests(PostgresFixture fixture)
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
        });
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record AccountDto(Guid Id, string Label);
    private sealed record NextAction(string Type, string Url, string Method, Dictionary<string, string> Fields);
    private sealed record PaymentDto(
        string Id, string Status, long AmountMinor, long? ChargedAmountMinor,
        int Installments, NextAction? NextAction);
    private sealed record RefundDto(string Id, long AmountMinor, string Status);

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
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

    private async Task<(TenantCreated Tenant, AccountDto Account)> SeedAsync()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Köprü Testi", slug = "kopru-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));
        var account = await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));
        return (tenant, account);
    }

    [Fact]
    public async Task Taksitli_odeme_vade_farkli_tutari_karttan_cekmeli_iade_tavani_cekilen_olmali()
    {
        var (tenant, account) = await SeedAsync();

        // Joker şema: 3 taksit → %10 vade farkı
        await SendOk<object>(HttpMethod.Post, "/v1/installments/schemes",
            new { connectorAccountId = account.Id, program = "*", installmentCount = 3, customerRateBps = 1_000 },
            ("X-Api-Key", tenant.ApiKey));

        // 10.000 kuruş mal bedeli, 3 taksit → karttan 11.000 çekilmeli
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, currency = "TRY", installments = 3, confirm = true },
            ("X-Api-Key", tenant.ApiKey));

        payment.Status.ShouldBe("requires_action");
        payment.AmountMinor.ShouldBe(10_000); // mal bedeli intent'te kalır
        payment.ChargedAmountMinor.ShouldBe(11_000); // vade farklı toplam denemede
        payment.NextAction!.Fields["mb_amount"].ShouldBe("11000"); // bankaya giden tutar

        // Banka dönüşü → succeeded
        var callback = await _client.PostAsync(payment.NextAction.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields));
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);

        var fetched = await SendOk<PaymentDto>(HttpMethod.Get, $"/v1/payments/{payment.Id}", null,
            ("X-Api-Key", tenant.ApiKey));
        fetched.Status.ShouldBe("succeeded");
        fetched.ChargedAmountMinor.ShouldBe(11_000);

        // İade tavanı ÇEKİLEN tutardır: tutarsız istek 400, tam iade 11.000
        var over = await Send(HttpMethod.Post, "/v1/refunds",
            new { paymentId = payment.Id, amountMinor = 11_001 }, ("X-Api-Key", tenant.ApiKey));
        over.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var refund = await SendOk<RefundDto>(HttpMethod.Post, "/v1/refunds",
            new { paymentId = payment.Id }, ("X-Api-Key", tenant.ApiKey));
        refund.AmountMinor.ShouldBe(11_000); // vade farkı dahil tam iade
        refund.Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task Programa_ozel_sema_jokeri_ezmeli()
    {
        var (tenant, account) = await SeedAsync();
        await SendOk<object>(HttpMethod.Post, "/v1/installments/schemes",
            new { connectorAccountId = account.Id, program = "*", installmentCount = 3, customerRateBps = 1_000 },
            ("X-Api-Key", tenant.ApiKey));
        await SendOk<object>(HttpMethod.Post, "/v1/installments/schemes",
            new { connectorAccountId = account.Id, program = "bonus", installmentCount = 3, customerRateBps = 0 },
            ("X-Api-Key", tenant.ApiKey));

        // Program ipucu "bonus" → vade farksız şema seçilir (joker %10 değil)
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, installments = 3, confirm = true, program = "bonus" },
            ("X-Api-Key", tenant.ApiKey));

        payment.ChargedAmountMinor.ShouldBe(10_000);
    }

    [Fact]
    public async Task Program_BIN_katalogundan_turetilmeli_program_ipucu_gerekmemeli()
    {
        var (tenant, account) = await SeedAsync();

        // Yalnız programa ÖZEL şema var — joker yok. Checkout yalnız BIN gönderir,
        // program ipucu göndermez; program kart kataloğundan türetilmezse ödeme
        // "bu taksidi kimse sunmuyor" diye boşuna reddedilirdi.
        await SendOk<object>(HttpMethod.Post, "/v1/installments/schemes",
            new { connectorAccountId = account.Id, program = "bonus", installmentCount = 3, customerRateBps = 500 },
            ("X-Api-Key", tenant.ApiKey));

        await SendOk<object>(HttpMethod.Post, "/v1/bins", new
        {
            bins = new[]
            {
                new
                {
                    bin = "540061", bankCode = "0062", bankName = "Garanti BBVA",
                    program = "bonus", brand = "mastercard", cardType = "credit", isCommercial = false,
                },
            },
        }, ("X-Platform-Key", AdminKey));

        var created = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, currency = "TRY", installments = 3 }, ("X-Api-Key", tenant.ApiKey));

        // program YOK, yalnız bin — checkout'un gönderdiği şeyin aynısı
        var confirmed = await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{created.Id}/confirm",
            new { bin = "540061" }, ("X-Api-Key", tenant.ApiKey));

        confirmed.Status.ShouldBe("requires_action");
        confirmed.ChargedAmountMinor.ShouldBe(10_500); // bonus şeması: %5 vade farkı
    }

    [Fact]
    public async Task Eksik_alanli_BIN_yuklemesi_400_donmeli()
    {
        // Hepsi NOT NULL sütun: doğrulama yakalamazsa istek 500'e düşerdi
        var response = await Send(HttpMethod.Post, "/v1/bins", new
        {
            bins = new[] { new { bin = "450803", bankName = "İş Bankası", program = "maximum" } },
        }, ("X-Platform-Key", AdminKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("bankCode");
        body.ShouldContain("brand");
        body.ShouldContain("cardType");
    }

    [Fact]
    public async Task Sema_yoksa_confirm_409_donmeli_ve_intent_created_kalmali()
    {
        var (tenant, _) = await SeedAsync(); // hiç şema tanımlanmadı

        var created = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, installments = 6 }, ("X-Api-Key", tenant.ApiKey));

        var confirm = await Send(HttpMethod.Post, $"/v1/payments/{created.Id}/confirm",
            new { }, ("X-Api-Key", tenant.ApiKey));
        confirm.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await confirm.Content.ReadAsStringAsync()).ShouldContain("installments.not_offered");

        // İntent banka reddine düşmedi — istemci taksidi düzeltip yeniden deneyebilir
        var fetched = await SendOk<PaymentDto>(HttpMethod.Get, $"/v1/payments/{created.Id}", null,
            ("X-Api-Key", tenant.ApiKey));
        fetched.Status.ShouldBe("created");

        // Tek çekim her zaman çalışır (şema gerektirmez)
        var single = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_000, installments = 1, confirm = true }, ("X-Api-Key", tenant.ApiKey));
        single.Status.ShouldBe("requires_action");
        single.ChargedAmountMinor.ShouldBe(10_000);
    }
}
