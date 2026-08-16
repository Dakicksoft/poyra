using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F2.3: Kasa (tokenizasyon) + direct akış — kart Poyra'dan geçer, 3DS yok.
/// Kart verisi hiçbir yanıtta/kayıtta düz görünmez; token'la tek tık ödeme çalışır.
/// </summary>
[Collection("postgres")]
public sealed class VaultAndDirectFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string TestVisa = "4111111111111111"; // yapısal geçerli örnek numara
    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _client;

    public VaultAndDirectFlowTests(PostgresFixture fixture)
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
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string ApiKey);
    private sealed record AccountDto(Guid Id, string Label);
    private sealed record CardTokenDto(
        string Token, string MaskedPan, string Brand, int ExpiryMonth, int ExpiryYear, string? CustomerRef);
    private sealed record ErrorDto(string Code, string? RawCode, string? Message);
    private sealed record PaymentDto(
        string Id, string Status, long AmountMinor, long? ChargedAmountMinor, ErrorDto? LastError);

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

    private async Task<(TenantCreated Tenant, AccountDto Account)> SeedAsync(bool broken = false)
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Kasa", slug = "kasa-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));

        var credentials = new Dictionary<string, string> { ["secret"] = "s3cret" };
        if (broken)
            credentials["fail_initiate"] = "true";

        var account = await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts",
            new { connectorKey = "mockbank", label = "Mock POS", credentials, priority = 1 },
            ("X-Api-Key", tenant.ApiKey));
        return (tenant, account);
    }

    [Fact]
    public async Task Kart_sakla_tokenla_ode_ve_kaldir()
    {
        var (tenant, _) = await SeedAsync();

        // Tokenize: yanıtta PAN yok, yalnız maske
        var card = await SendOk<CardTokenDto>(HttpMethod.Post, "/v1/vault/cards", new
        {
            cardNumber = TestVisa,
            expiryMonth = 12,
            expiryYear = 2031,
            holderName = "AYSE YILMAZ",
            customerRef = "musteri-1",
        }, ("X-Api-Key", tenant.ApiKey));

        card.Token.ShouldStartWith("tok_");
        card.MaskedPan.ShouldBe("411111******1111");
        card.Brand.ShouldBe("visa");

        // Aynı kart yeniden saklanırsa aynı token döner (çift kayıt yok)
        var again = await SendOk<CardTokenDto>(HttpMethod.Post, "/v1/vault/cards",
            new { cardNumber = TestVisa, expiryMonth = 12, expiryYear = 2031, customerRef = "musteri-1" },
            ("X-Api-Key", tenant.ApiKey));
        again.Token.ShouldBe(card.Token);

        // DB'de düz PAN YOK — zarf şifreli (PCI kanıtı)
        await using (var db = _fixture.CreateVault(PostgresFixture.TenantCtx(tenant.TenantId)))
        {
            var stored = await db.CardTokens.AsNoTracking().SingleAsync();
            System.Text.Encoding.UTF8.GetString(stored.CardEncrypted).ShouldNotContain("411111");
            stored.MaskedPan.ShouldBe("411111******1111");
        }

        // Token'la tek tık ödeme (CVV'siz — tekrarlayan ödeme kalıbı)
        var created = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 25_000, currency = "TRY" }, ("X-Api-Key", tenant.ApiKey));
        var paid = await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{created.Id}/confirm-direct",
            new { cardToken = card.Token }, ("X-Api-Key", tenant.ApiKey));

        paid.Status.ShouldBe("succeeded"); // direct: 3DS yok, anında sonuç
        paid.ChargedAmountMinor.ShouldBe(25_000);

        // Kaldır: kayıt kalır, zarf boşalır (kriptografik imha)
        (await Send(HttpMethod.Delete, $"/v1/vault/cards/{card.Token}", null,
            ("X-Api-Key", tenant.ApiKey))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendOk<List<CardTokenDto>>(HttpMethod.Get, "/v1/vault/cards", null,
            ("X-Api-Key", tenant.ApiKey))).ShouldBeEmpty();

        await using (var db = _fixture.CreateVault(PostgresFixture.TenantCtx(tenant.TenantId)))
        {
            var stored = await db.CardTokens.AsNoTracking().SingleAsync();
            stored.DeletedAt.ShouldNotBeNull();
            stored.CardEncrypted.ShouldBeEmpty(); // zarf imha edildi
        }

        // Kaldırılan token'la ödeme reddedilir
        var after = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 1_000 }, ("X-Api-Key", tenant.ApiKey));
        var denied = await Send(HttpMethod.Post, $"/v1/payments/{after.Id}/confirm-direct",
            new { cardToken = card.Token }, ("X-Api-Key", tenant.ApiKey));
        denied.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Direct_ile_kart_bilgisi_gonderme_ve_ret_yolu()
    {
        var (tenant, _) = await SeedAsync();

        // Başarılı: ham kart + CVV (kendi formumuz senaryosu)
        var created = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 12_300 }, ("X-Api-Key", tenant.ApiKey));
        var paid = await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{created.Id}/confirm-direct",
            new { cardNumber = TestVisa, expiryMonth = 12, expiryYear = 2031, cvv = "123" },
            ("X-Api-Key", tenant.ApiKey));
        paid.Status.ShouldBe("succeeded");

        // Kart reddi (tutar %100==99): failover YAPILMAZ, terminal
        var declinedIntent = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 10_099 }, ("X-Api-Key", tenant.ApiKey));
        var declined = await SendOk<PaymentDto>(HttpMethod.Post,
            $"/v1/payments/{declinedIntent.Id}/confirm-direct",
            new { cardNumber = TestVisa, expiryMonth = 12, expiryYear = 2031, cvv = "123" },
            ("X-Api-Key", tenant.ApiKey));
        declined.Status.ShouldBe("failed");
        declined.LastError!.Code.ShouldBe("poyra.card_declined");
        declined.LastError.RawCode.ShouldBe("05");

        // Geçersiz kart numarası doğrulamada düşer (bankaya gitmez)
        var badIntent = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 5_000 }, ("X-Api-Key", tenant.ApiKey));
        var bad = await Send(HttpMethod.Post, $"/v1/payments/{badIntent.Id}/confirm-direct",
            new { cardNumber = "4111111111111112", expiryMonth = 12, expiryYear = 2031 },
            ("X-Api-Key", tenant.ApiKey));
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync()).ShouldContain("Luhn");

        // Kart + token birlikte gönderilemez
        var conflict = await Send(HttpMethod.Post, $"/v1/payments/{badIntent.Id}/confirm-direct",
            new { cardNumber = TestVisa, expiryMonth = 12, expiryYear = 2031, cardToken = "tok_x" },
            ("X-Api-Key", tenant.ApiKey));
        conflict.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Kart_numarasi_hic_gonderilmezse_400_donmeli()
    {
        var (tenant, _) = await SeedAsync();

        // Alanı unutan entegrasyon 400 + alan adı görmeli; 500 "bizde bir sorun var"
        // demektir ve geliştiriciyi yanlış yere bakmaya gönderir.
        var missing = await Send(HttpMethod.Post, "/v1/vault/cards",
            new { expiryMonth = 12, expiryYear = 2031 }, ("X-Api-Key", tenant.ApiKey));

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync()).ShouldContain("cardNumber");
    }

    [Fact]
    public async Task Suresi_gecmis_kart_saklanmamali_ve_kasa_isyerleri_arasi_gorunmemeli()
    {
        var (tenantA, _) = await SeedAsync();
        var (tenantB, _) = await SeedAsync();

        var expired = await Send(HttpMethod.Post, "/v1/vault/cards",
            new { cardNumber = TestVisa, expiryMonth = 1, expiryYear = 2020 },
            ("X-Api-Key", tenantA.ApiKey));
        expired.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await expired.Content.ReadAsStringAsync()).ShouldContain("vault.card_expired");

        await SendOk<CardTokenDto>(HttpMethod.Post, "/v1/vault/cards",
            new { cardNumber = TestVisa, expiryMonth = 12, expiryYear = 2031 },
            ("X-Api-Key", tenantA.ApiKey));

        // İşyeri B, A'nın kartını görmez (Katman A + B)
        (await SendOk<List<CardTokenDto>>(HttpMethod.Get, "/v1/vault/cards", null,
            ("X-Api-Key", tenantB.ApiKey))).ShouldBeEmpty();

        // Katman B tek başına: ham SQL ile bile görünmemeli
        await using var db = _fixture.CreateVault(PostgresFixture.TenantCtx(tenantB.TenantId));
        var raw = await db.Database
            .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM card_tokens""")
            .SingleAsync();
        raw.ShouldBe(0L);
    }

    [Fact]
    public async Task Direct_akista_erisim_hatasi_failover_tetiklemeli()
    {
        // Birinci hesap bozuk (fail_initiate → ConnectorUnavailable), ikincisi sağlam
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "DirectFailover", slug = "df-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));
        await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Bozuk POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s", ["fail_initiate"] = "true" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));
        var healthy = await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Sağlam POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 2,
        }, ("X-Api-Key", tenant.ApiKey));

        var created = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 33_000 }, ("X-Api-Key", tenant.ApiKey));
        var paid = await SendOk<PaymentDto>(HttpMethod.Post, $"/v1/payments/{created.Id}/confirm-direct",
            new { cardNumber = TestVisa, expiryMonth = 12, expiryYear = 2031, cvv = "123" },
            ("X-Api-Key", tenant.ApiKey));

        paid.Status.ShouldBe("succeeded"); // ikinci hesap devraldı

        await using var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId));
        var attempts = await db.PaymentAttempts.AsNoTracking().OrderBy(a => a.AttemptNo).ToListAsync();
        attempts.Count.ShouldBe(2);
        attempts[0].ErrorUnifiedCode.ShouldBe("poyra.connector_unavailable");
        attempts[1].ConnectorAccountId.ShouldBe(healthy.Id);
    }
}
