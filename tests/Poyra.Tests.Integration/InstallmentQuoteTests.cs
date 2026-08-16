using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F1.3 BIN &amp; Taksit: platform BIN kataloğu yükler → işyeri şema tanımlar →
/// quote, kartın ilk 6-8 hanesiyle hesap bazında taksit tablosu üretir.
/// Debit karta taksit önerilmez; bilinmeyen BIN yalnız tek çekim döner.
/// </summary>
[Collection("postgres")]
public sealed class InstallmentQuoteTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _client;

    public InstallmentQuoteTests(PostgresFixture fixture)
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
    private sealed record BinDto(string Bin, string BankName, string Program, string Brand, string CardType);
    private sealed record OptionDto(
        Guid ConnectorAccountId, string AccountLabel, int InstallmentCount,
        int CustomerRateBps, long TotalAmountMinor, long MonthlyAmountMinor, long LastMonthAmountMinor);
    private sealed record QuoteResponse(long AmountMinor, string Currency, BinDto? Bin, List<OptionDto> Options);

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

    // Not: test BIN'leri temsili/örnek değerlerdir — gerçek katalog F2'de dış kaynaktan yüklenir
    private async Task SeedBinsAsync()
        => await SendOk<object>(HttpMethod.Post, "/v1/bins", new
        {
            bins = new object[]
            {
                new { bin = "540061", bankCode = "0062", bankName = "Örnek Banka A", program = "bonus", brand = "mastercard", cardType = "credit", isCommercial = false },
                new { bin = "979200", bankCode = "0111", bankName = "Örnek Banka B", program = "bankkart", brand = "troy", cardType = "debit", isCommercial = false },
            },
        }, ("X-Platform-Key", AdminKey));

    private async Task<(TenantCreated Tenant, AccountDto AccountA, AccountDto AccountB)> SeedTenantAsync()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Taksit Testi", slug = "taksit-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));

        async Task<AccountDto> AddAccount(string label, int priority)
            => await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
            {
                connectorKey = "mockbank",
                label,
                credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
                priority,
            }, ("X-Api-Key", tenant.ApiKey));

        return (tenant, await AddAccount("POS A", 1), await AddAccount("POS B", 2));
    }

    [Fact]
    public async Task Kredi_karti_binine_sema_bazli_taksit_tablosu_donmeli()
    {
        await SeedBinsAsync();
        var (tenant, accountA, accountB) = await SeedTenantAsync();

        // Şemalar: A×bonus×3 → %3,50; A×*×6 → %9,00; B×bonus×3 → vade farksız
        foreach (var scheme in new object[]
        {
            new { connectorAccountId = accountA.Id, program = "bonus", installmentCount = 3, customerRateBps = 350 },
            new { connectorAccountId = accountA.Id, program = "*", installmentCount = 6, customerRateBps = 900 },
            new { connectorAccountId = accountB.Id, program = "bonus", installmentCount = 3, customerRateBps = 0 },
        })
            await SendOk<object>(HttpMethod.Post, "/v1/installments/schemes", scheme, ("X-Api-Key", tenant.ApiKey));

        var quote = await SendOk<QuoteResponse>(HttpMethod.Post, "/v1/installments/quote",
            new { bin = "540061", amountMinor = 10_000 }, ("X-Api-Key", tenant.ApiKey));

        quote.Bin.ShouldNotBeNull();
        quote.Bin.Program.ShouldBe("bonus");

        // 2 tek çekim + A-3 + A-6(joker) + B-3 = 5 seçenek
        quote.Options.Count.ShouldBe(5);
        quote.Options.Count(o => o.InstallmentCount == 1).ShouldBe(2);

        var a3 = quote.Options.Single(o => o.ConnectorAccountId == accountA.Id && o.InstallmentCount == 3);
        a3.TotalAmountMinor.ShouldBe(10_350); // %3,50 vade farkı
        a3.MonthlyAmountMinor.ShouldBe(3_450);
        a3.LastMonthAmountMinor.ShouldBe(3_450);

        var a6 = quote.Options.Single(o => o.ConnectorAccountId == accountA.Id && o.InstallmentCount == 6);
        a6.TotalAmountMinor.ShouldBe(10_900); // "*" joker şeması
        (a6.MonthlyAmountMinor * 5 + a6.LastMonthAmountMinor).ShouldBe(10_900); // kuruş kaybolmaz

        quote.Options.Single(o => o.ConnectorAccountId == accountB.Id && o.InstallmentCount == 3)
            .TotalAmountMinor.ShouldBe(10_000); // vade farksız
    }

    [Fact]
    public async Task Debit_karta_ve_bilinmeyen_bine_taksit_onerilmemeli()
    {
        await SeedBinsAsync();
        var (tenant, _, _) = await SeedTenantAsync();
        await SendOk<object>(HttpMethod.Post, "/v1/installments/schemes",
            new { connectorAccountId = (await SendOk<List<AccountDto>>(HttpMethod.Get, "/v1/connector-accounts", null, ("X-Api-Key", tenant.ApiKey)))[0].Id, program = "*", installmentCount = 3, customerRateBps = 0 },
            ("X-Api-Key", tenant.ApiKey));

        // Debit (bankkart/troy) → yalnız tek çekim
        var debit = await SendOk<QuoteResponse>(HttpMethod.Post, "/v1/installments/quote",
            new { bin = "979200", amountMinor = 5_000 }, ("X-Api-Key", tenant.ApiKey));
        debit.Bin!.CardType.ShouldBe("debit");
        debit.Options.ShouldAllBe(o => o.InstallmentCount == 1);

        // Katalogda olmayan BIN → bin=null, yalnız tek çekim
        var unknown = await SendOk<QuoteResponse>(HttpMethod.Post, "/v1/installments/quote",
            new { bin = "999999", amountMinor = 5_000 }, ("X-Api-Key", tenant.ApiKey));
        unknown.Bin.ShouldBeNull();
        unknown.Options.ShouldAllBe(o => o.InstallmentCount == 1);
    }

    [Fact]
    public async Task Bin_katalogu_platform_anahtari_ister_isyeri_sorgulayabilir()
    {
        await SeedBinsAsync();
        var (tenant, _, _) = await SeedTenantAsync();

        // Platform anahtarsız yükleme → 401
        (await Send(HttpMethod.Post, "/v1/bins", new { bins = Array.Empty<object>() },
            ("X-Api-Key", tenant.ApiKey))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // İşyeri BIN sorgular (8 haneli girişte 6'lık kayda düşer)
        var bin = await SendOk<BinDto>(HttpMethod.Get, "/v1/bins/54006112", null, ("X-Api-Key", tenant.ApiKey));
        bin.Bin.ShouldBe("540061");
        bin.Program.ShouldBe("bonus");
    }
}
