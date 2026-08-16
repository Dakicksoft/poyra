using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Api;
using Poyra.SharedKernel.Tenancy;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// Beklenen para defteri (F1) — farklılaşma tezinin ilk halkası.
///
/// Zincir: tahsilat → <b>alacak</b> → POS ekstresi teyidi → özet.
/// Sorulan soru "hangi POS'tan geçti" değil, <b>"bankadan alacağım ne, ne zaman
/// gelecek, doğru mu geldi"</b>.
/// </summary>
[Collection("postgres")]
public sealed class ReceivableLedgerTests : IDisposable
{
    private const string AdminKey = "test-admin-key";

    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _api;
    private readonly HttpClient _apiNoRedirect;

    public ReceivableLedgerTests(PostgresFixture fixture)
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
        _apiNoRedirect = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record AccountDto(Guid Id);
    private sealed record PaymentDto(string Id, string Status, NextActionDto? NextAction);
    private sealed record NextActionDto(string Url, string Method, Dictionary<string, string> Fields);
    private sealed record ReceivableDto(
        string Id, Guid ConnectorAccountId, string Kind, string Status,
        string AttemptId, string? PaymentId, long GrossMinor, string Currency, int Installments,
        int? ExpectedRateBps, long? ExpectedCommissionMinor, long? ExpectedNetMinor,
        DateOnly? ExpectedValueDate, long? ConfirmedCommissionMinor, long? ConfirmedNetMinor,
        DateOnly? ConfirmedValueDate, long? CommissionDeltaMinor, int? ValueDateDelayDays,
        DateTimeOffset CapturedAtServer);
    private sealed record AccountRow(
        Guid ConnectorAccountId, int Count, long OutstandingMinor, long OverdueMinor,
        long CommissionDeltaMinor, int MaxDelayDays);
    private sealed record SummaryDto(
        DateOnly Today, long TotalOutstandingMinor, long DueTodayMinor, long OverdueMinor,
        int OverdueCount, long ConfirmedMinor, int UnknownTermsCount, long CommissionDeltaMinor,
        List<AccountRow> Accounts);

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

    private async Task<T> ApiOk<T>(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);

        var response = await _api.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(TenantCreated Tenant, Guid AccountId)> SeedAsync()
    {
        var tenant = await ApiOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Defter A.Ş.",
            slug = "dft-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = $"sahip-{Guid.NewGuid():N}@ornek.com",
            ownerPassword = "defter-parola-123",
            ownerName = "Sahip",
        }, ("X-Platform-Key", AdminKey));

        var account = await ApiOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        return (tenant, account.Id);
    }

    /// <summary>Anlaşma: oran + valör. Beklenen tutarın TEK dayanağı budur.</summary>
    private async Task AgreeAsync(string apiKey, Guid accountId, int rateBps, int valorDays)
        => await ApiOk<object>(HttpMethod.Post, "/v1/recon/agreements", new
        {
            connectorAccountId = accountId,
            installmentCount = 1,
            rateBps,
            valorDays,
        }, ("X-Api-Key", apiKey));

    /// <summary>Gerçek ödeme akışı: create → confirm → banka formu → callback.</summary>
    private async Task<string> PayAsync(string apiKey, long amountMinor)
    {
        var created = await ApiOk<PaymentDto>(HttpMethod.Post, "/v1/payments", new
        {
            amountMinor,
            currency = "TRY",
            description = "Defter testi",
        }, ("X-Api-Key", apiKey));

        var confirmed = await ApiOk<PaymentDto>(
            HttpMethod.Post, $"/v1/payments/{created.Id}/confirm", new { }, ("X-Api-Key", apiKey));

        confirmed.NextAction.ShouldNotBeNull();
        var callback = await _apiNoRedirect.PostAsync(confirmed.NextAction!.Url,
            new FormUrlEncodedContent(confirmed.NextAction.Fields));
        callback.IsSuccessStatusCode.ShouldBeTrue(await callback.Content.ReadAsStringAsync());

        return created.Id;
    }

    private async Task ProjectAsync()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();
        await scope.ServiceProvider
            .GetRequiredService<Poyra.Modules.Ledger.Infrastructure.ReceivableProjectionJob>()
            .ProjectAsync();
    }

    // ------------------------------------------------------------------ zincirin ilk halkası

    [Fact]
    public async Task Tahsilat_bankadan_ALACAGA_donusmeli()
    {
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, rateBps: 250, valorDays: 1);

        await PayAsync(tenant.ApiKey, 100_00); // 100,00 ₺
        await ProjectAsync();

        var rows = await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey));

        var receivable = rows.ShouldHaveSingleItem();
        receivable.Id.ShouldStartWith("rcv_");
        receivable.Kind.ShouldBe("sale");
        receivable.Status.ShouldBe("expected");

        // Beklenen tutar ANLAŞMADAN hesaplandı — bu bizim iddiamız, bankanın değil
        receivable.ExpectedRateBps.ShouldBe(250);
        receivable.ExpectedCommissionMinor.ShouldBe(250);   // 100,00 ₺ · %2,50 = 2,50 ₺
        receivable.ExpectedNetMinor.ShouldBe(9_750);
        receivable.ExpectedValueDate.ShouldNotBeNull();

        // Banka henüz konuşmadı: teyit alanları BOŞ, fark da null (sıfır DEĞİL)
        receivable.ConfirmedCommissionMinor.ShouldBeNull();
        receivable.CommissionDeltaMinor.ShouldBeNull();
    }

    [Fact]
    public async Task Anlasma_YOKSA_beklenen_tutar_UYDURULMAMALI()
    {
        // Uydurulmuş bir oran, olmayan bir farkı varmış gibi gösterir ve işyerine
        // bankaya haksız itiraz yazdırır.
        var (tenant, _) = await SeedAsync();

        await PayAsync(tenant.ApiKey, 250_00);
        await ProjectAsync();

        var receivable = (await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey))).ShouldHaveSingleItem();

        receivable.ExpectedRateBps.ShouldBeNull();
        receivable.ExpectedCommissionMinor.ShouldBeNull();
        receivable.ExpectedValueDate.ShouldBeNull();

        // Ama alacağın KENDİSİ kaydedilmiş: brüt biliniyor
        receivable.GrossMinor.ShouldBe(25_000);

        // Özette "şartı bilinmeyen" olarak ayrıca sayılır — sessizce yok sayılmaz
        var summary = await ApiOk<SummaryDto>(
            HttpMethod.Get, "/v1/receivables/summary", null, ("X-Api-Key", tenant.ApiKey));
        summary.UnknownTermsCount.ShouldBe(1);
        summary.TotalOutstandingMinor.ShouldBe(25_000); // net bilinmiyorsa brütten sayılır
    }

    [Fact]
    public async Task Projeksiyon_yeniden_kossa_da_alacak_IKI_KEZ_yazilmamali()
    {
        // İş yeniden koşabilir (deploy, çökme, pencere kayması). Tekillik olmasaydı
        // işyerine olmayan para borç görünürdü.
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, 250, 1);
        await PayAsync(tenant.ApiKey, 500_00);

        for (var i = 0; i < 3; i++)
            await ProjectAsync();

        var rows = await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey));

        rows.Count.ShouldBe(1);
        rows[0].GrossMinor.ShouldBe(50_000);
    }

    // ------------------------------------------------------------------ banka teyidi

    [Fact]
    public async Task Ekstre_eslesince_banka_TEYIDI_deftere_islenmeli()
    {
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, rateBps: 250, valorDays: 1);

        var paymentId = await PayAsync(tenant.ApiKey, 1_000_00); // 1.000,00 ₺
        await ProjectAsync();

        var before = (await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey))).ShouldHaveSingleItem();
        before.Status.ShouldBe("expected");
        before.ExpectedCommissionMinor.ShouldBe(2_500); // %2,50

        // Banka ekstresi: GERÇEK komisyon 31,00 ₺ — anlaşmadan 6,00 ₺ FAZLA.
        // poyra_csv tutarları KURUŞ tam sayı ister (banka-özel biçimler ayrı ayrıştırıcıda).
        var orderId = before.AttemptId;
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var csv = "order_id;type;gross;commission;net;value_date\n"
                  + $"{orderId};sale;100000;3100;96900;{day:yyyy-MM-dd}\n";

        await UploadStatementAsync(tenant.ApiKey, accountId, day, csv);

        var after = (await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey))).ShouldHaveSingleItem();

        after.Status.ShouldBe("bank_confirmed");
        after.ConfirmedCommissionMinor.ShouldBe(3_100);
        after.ConfirmedNetMinor.ShouldBe(96_900);

        // ASIL DEĞER: iki sayı ayrı duruyor ve fark görünüyor
        after.ExpectedCommissionMinor.ShouldBe(2_500);
        after.CommissionDeltaMinor.ShouldBe(600); // banka 6,00 ₺ fazla kesmiş

        var summary = await ApiOk<SummaryDto>(
            HttpMethod.Get, "/v1/receivables/summary", null, ("X-Api-Key", tenant.ApiKey));
        summary.CommissionDeltaMinor.ShouldBe(600);
        summary.ConfirmedMinor.ShouldBe(97_500); // beklenen net üzerinden
    }

    private async Task UploadStatementAsync(string apiKey, Guid accountId, DateOnly day, string csv)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "ekstre.csv");
        content.Add(new StringContent(accountId.ToString()), "connectorAccountId");
        content.Add(new StringContent(day.ToString("yyyy-MM-dd")), "statementDate");
        content.Add(new StringContent("poyra_csv"), "format");

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/recon/statements/upload")
        {
            Content = content,
        };
        request.Headers.Add("X-Api-Key", apiKey);

        var response = await _api.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------ iade

    [Fact]
    public async Task Iade_alacagi_AZALTMALI()
    {
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, 250, 1);

        var paymentId = await PayAsync(tenant.ApiKey, 400_00);
        await ApiOk<object>(HttpMethod.Post, "/v1/refunds", new
        {
            paymentId,
            amountMinor = 100_00, // kısmi iade
            reason = "Müşteri vazgeçti",
        }, ("X-Api-Key", tenant.ApiKey));

        await ProjectAsync();

        var rows = await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey));

        rows.Count.ShouldBe(2);
        var refund = rows.Single(r => r.Kind == "refund");

        // İade NEGATİF yazılır ki toplam doğru çıksın
        refund.GrossMinor.ShouldBe(-10_000);
        refund.ExpectedCommissionMinor.ShouldBe(-250);

        var summary = await ApiOk<SummaryDto>(
            HttpMethod.Get, "/v1/receivables/summary", null, ("X-Api-Key", tenant.ApiKey));

        // 400,00 net (39.000) − 100,00 net (9.750) = 29.250
        summary.TotalOutstandingMinor.ShouldBe(29_250);
    }

    // ------------------------------------------------------------------ işyeri yalıtımı

    [Fact]
    public async Task Baska_isyerinin_alacagi_GORUNMEMELI()
    {
        var (first, firstAccount) = await SeedAsync();
        var (second, _) = await SeedAsync();

        await AgreeAsync(first.ApiKey, firstAccount, 250, 1);
        await PayAsync(first.ApiKey, 900_00);
        await ProjectAsync();

        (await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", second.ApiKey))).ShouldBeEmpty();

        var summary = await ApiOk<SummaryDto>(
            HttpMethod.Get, "/v1/receivables/summary", null, ("X-Api-Key", second.ApiKey));
        summary.TotalOutstandingMinor.ShouldBe(0);
    }

    // ================================================================== F2: üç yönlü mutabakat

    private sealed record SettlementStatementDto(
        string Id, Guid ConnectorAccountId, string Format, string FileName,
        int LineCount, DateOnly? FromDate, DateOnly? ToDate, DateTimeOffset CreatedAt);
    private sealed record FindingDto(
        Guid ConnectorAccountId, DateOnly ValueDate, string Outcome,
        long ExpectedMinor, long ActualMinor, long DeltaMinor, int ReceivableCount,
        DateTimeOffset DetectedAt);
    private sealed record SettlementReportDto(
        long ShortfallMinor, long OverpaidMinor, int SettledDays, int ShortDays,
        int MissingDays, List<FindingDto> Findings);

    private async Task<SettlementStatementDto> UploadAccountStatementAsync(
        string apiKey, Guid accountId, string format, string content)
    {
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        body.Add(file, "file", "hesap-ekstresi.txt");
        body.Add(new StringContent(accountId.ToString()), "connectorAccountId");
        body.Add(new StringContent(format), "format");

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/settlements/upload") { Content = body };
        request.Headers.Add("X-Api-Key", apiKey);

        var response = await _api.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SettlementStatementDto>())!;
    }

    /// <summary>Alacakların beklenen valör günü ve net toplamı — ekstre o güne yazılacak.</summary>
    private async Task<(DateOnly ValueDate, long NetMinor)> ExpectedSettlementAsync(string apiKey)
    {
        var rows = await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", apiKey));

        var day = rows.Select(r => r.ExpectedValueDate!.Value).Distinct().ShouldHaveSingleItem();
        return (day, rows.Sum(r => r.ExpectedNetMinor ?? 0));
    }

    [Fact]
    public async Task Soz_verilen_para_GELDIYSE_alacak_kapanmali()
    {
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, rateBps: 250, valorDays: 1);

        await PayAsync(tenant.ApiKey, 1_000_00);
        await PayAsync(tenant.ApiKey, 500_00);
        await ProjectAsync();

        var (valueDate, expectedNet) = await ExpectedSettlementAsync(tenant.ApiKey);
        expectedNet.ShouldBe(146_250); // (100.000 + 50.000) − %2,50

        // Banka hasılatı TOPLU yatırdı — tek satır, iki tahsilatın neti
        await UploadAccountStatementAsync(tenant.ApiKey, accountId, "poyra_csv",
            $"value_date;amount;description;reference\n{valueDate:yyyy-MM-dd};{expectedNet};POS HASILAT;REF1\n");

        var report = await ApiOk<SettlementReportDto>(
            HttpMethod.Get, "/v1/settlements/report", null, ("X-Api-Key", tenant.ApiKey));

        report.SettledDays.ShouldBe(1);
        report.ShortfallMinor.ShouldBe(0);
        report.Findings.ShouldHaveSingleItem().Outcome.ShouldBe("settled");

        // ZİNCİRİN SON HALKASI: alacaklar kapandı
        var rows = await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey));
        rows.ShouldAllBe(r => r.Status == "settled");

        var summary = await ApiOk<SummaryDto>(
            HttpMethod.Get, "/v1/receivables/summary", null, ("X-Api-Key", tenant.ApiKey));
        summary.TotalOutstandingMinor.ShouldBe(0);
    }

    [Fact]
    public async Task Banka_EKSIK_yatirirsa_alacak_KAPANMAMALI()
    {
        // Ürünün varlık sebebi: POS ekstresi "ödeyeceğim" der, hesap ekstresi
        // "ödedim" der. Tutmadığında fark BANKADAN SORULACAK PARADIR.
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, 250, 1);

        await PayAsync(tenant.ApiKey, 1_000_00);
        await ProjectAsync();

        var (valueDate, expectedNet) = await ExpectedSettlementAsync(tenant.ApiKey);
        expectedNet.ShouldBe(97_500);

        // Banka 500,00 ₺ EKSİK yatırdı
        await UploadAccountStatementAsync(tenant.ApiKey, accountId, "poyra_csv",
            $"value_date;amount\n{valueDate:yyyy-MM-dd};{expectedNet - 50_000}\n");

        var report = await ApiOk<SettlementReportDto>(
            HttpMethod.Get, "/v1/settlements/report", null, ("X-Api-Key", tenant.ApiKey));

        report.ShortDays.ShouldBe(1);
        report.ShortfallMinor.ShouldBe(50_000); // 500,00 ₺ bankadan sorulacak

        var finding = report.Findings.ShouldHaveSingleItem();
        finding.Outcome.ShouldBe("short");
        finding.ExpectedMinor.ShouldBe(97_500);
        finding.ActualMinor.ShouldBe(47_500);
        finding.DeltaMinor.ShouldBe(-50_000);

        // ALACAK KAPANMADI: kapatmak, bankadan sorulacak parayı "tahsil edildi"
        // diye gömmek olurdu
        var rows = await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey));
        rows.ShouldHaveSingleItem().Status.ShouldNotBe("settled");
    }

    [Fact]
    public async Task Hic_para_yatmayan_gun_MISSING_olarak_bildirilmeli()
    {
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, 250, 1);

        await PayAsync(tenant.ApiKey, 800_00);
        await ProjectAsync();

        var (valueDate, expectedNet) = await ExpectedSettlementAsync(tenant.ApiKey);

        await UploadAccountStatementAsync(tenant.ApiKey, accountId, "poyra_csv",
            $"value_date;amount;description\n{valueDate:yyyy-MM-dd};0;HAREKET YOK\n");

        var report = await ApiOk<SettlementReportDto>(
            HttpMethod.Get, "/v1/settlements/report", null, ("X-Api-Key", tenant.ApiKey));

        report.MissingDays.ShouldBe(1);
        report.ShortfallMinor.ShouldBe(expectedNet);
        report.Findings[0].Outcome.ShouldBe("missing");
    }

    [Fact]
    public async Task MT940_ekstresi_okunup_eslesmeli()
    {
        // TR kurumsal bankacılığın fiilî standardı
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, 250, 1);

        await PayAsync(tenant.ApiKey, 1_000_00);
        await ProjectAsync();

        var (valueDate, expectedNet) = await ExpectedSettlementAsync(tenant.ApiKey);

        // MT940: valör YYMMDD, tutar ondalık VİRGÜLLE
        var yymmdd = valueDate.ToString("yyMMdd");
        var amount = $"{expectedNet / 100},{expectedNet % 100:00}";
        var mt940 = $":20:EKSTRE\n:61:{yymmdd}C{amount}NTRFNONREF//POS1\n:86:POS HASILAT\n";

        var statement = await UploadAccountStatementAsync(tenant.ApiKey, accountId, "mt940", mt940);
        statement.Id.ShouldStartWith("set_");
        statement.LineCount.ShouldBe(1);

        var report = await ApiOk<SettlementReportDto>(
            HttpMethod.Get, "/v1/settlements/report", null, ("X-Api-Key", tenant.ApiKey));

        report.SettledDays.ShouldBe(1);
        report.ShortfallMinor.ShouldBe(0);
    }

    [Fact]
    public async Task Bozuk_ekstre_SESSIZCE_yutulmamali()
    {
        // Yarım okunmuş bir ekstre, olmayan bir "eksik yatırma" bulgusu üretir
        var (tenant, accountId) = await SeedAsync();

        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(":61:BOZUKVERI\n")),
            "file", "bozuk.txt");
        body.Add(new StringContent(accountId.ToString()), "connectorAccountId");
        body.Add(new StringContent("mt940"), "format");

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/settlements/upload") { Content = body };
        request.Headers.Add("X-Api-Key", tenant.ApiKey);

        var response = await _api.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("settlement.parse_error");
    }

    // ================================================================== F3 + F4

    private sealed record ValueDateCostRowDto(
        Guid ConnectorAccountId, int LateCount, long LateAmountMinor,
        int TotalDelayDays, int MaxDelayDays, long CostMinor);
    private sealed record ValueDateCostDto(
        int? RateBps, long TotalCostMinor, int LateCount, long LateAmountMinor,
        double AverageDelayDays, int EarlyCount, List<ValueDateCostRowDto> Accounts);
    private sealed record ClaimDto(
        string Id, Guid ConnectorAccountId, DateOnly PeriodFrom, DateOnly PeriodTo,
        long ClaimedMinor, long RecoveredMinor, long OutstandingMinor, int FindingCount,
        string Status, string? BankReference, DateTimeOffset? SubmittedAt,
        DateTimeOffset? RespondedAt, DateTimeOffset? ClosedAt, DateTimeOffset CreatedAt);
    private sealed record ClaimEventDto(string EventType, string? Note, long? AmountMinor, DateTimeOffset At);
    private sealed record ClaimSummaryDto(
        long OpenClaimedMinor, long RecoveredThisYearMinor, int OpenCount, int ClosedCount,
        List<ClaimDto> Claims);

    [Fact]
    public async Task Oran_tanimsizsa_valor_maliyeti_HESAPLANMAMALI()
    {
        // Uydurulmuş bir oran, olmayan bir zararı varmış gibi gösterir
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, 250, 1);
        await PayAsync(tenant.ApiKey, 1_000_00);
        await ProjectAsync();

        var report = await ApiOk<ValueDateCostDto>(
            HttpMethod.Get, "/v1/ledger/value-date-cost", null, ("X-Api-Key", tenant.ApiKey));

        report.RateBps.ShouldBeNull();
        report.TotalCostMinor.ShouldBe(0);
    }

    [Fact]
    public async Task Gec_odenen_alacak_FINANSMAN_MALIYETI_uretmeli()
    {
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, rateBps: 250, valorDays: 1);

        // İşyerinin yıllık finansman maliyeti: %35
        await ApiOk<object>(HttpMethod.Post, "/v1/ledger/settings",
            new { annualFinancingRateBps = 3_500 }, ("X-Api-Key", tenant.ApiKey));

        await PayAsync(tenant.ApiKey, 100_000_00); // 100.000,00 ₺
        await ProjectAsync();

        var before = (await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey))).ShouldHaveSingleItem();

        // Banka POS ekstresinde valörü 3 gün GEÇ bildirdi
        var lateValueDate = before.ExpectedValueDate!.Value.AddDays(3);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var csv = "order_id;type;gross;commission;net;value_date\n"
                  + $"{before.AttemptId};sale;10000000;250000;9750000;{lateValueDate:yyyy-MM-dd}\n";
        await UploadStatementAsync(tenant.ApiKey, accountId, day, csv);

        var report = await ApiOk<ValueDateCostDto>(
            HttpMethod.Get, "/v1/ledger/value-date-cost", null, ("X-Api-Key", tenant.ApiKey));

        report.RateBps.ShouldBe(3_500);
        report.LateCount.ShouldBe(1);
        report.AverageDelayDays.ShouldBe(3);

        // 97.500,00 ₺ net × %35 / 365 × 3 gün ≈ 280,48 ₺
        // "Banka 3 gün geç ödedi" değil, "3 gün gecikme size 280,48 ₺'ye mal oldu"
        report.TotalCostMinor.ShouldBe(28_048);
    }

    [Fact]
    public async Task Erken_odeme_gecikme_maliyetiyle_NETLESTIRILMEMELI()
    {
        // Ara sıra erken ödeyen bir banka, kronik gecikmesini gizleyebilirdi
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, 250, 5);
        await ApiOk<object>(HttpMethod.Post, "/v1/ledger/settings",
            new { annualFinancingRateBps = 3_500 }, ("X-Api-Key", tenant.ApiKey));

        await PayAsync(tenant.ApiKey, 1_000_00);
        await ProjectAsync();

        var row = (await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey))).ShouldHaveSingleItem();

        // Banka 2 gün ERKEN ödedi
        var earlyDate = row.ExpectedValueDate!.Value.AddDays(-2);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        await UploadStatementAsync(tenant.ApiKey, accountId, day,
            "order_id;type;gross;commission;net;value_date\n"
            + $"{row.AttemptId};sale;100000;2500;97500;{earlyDate:yyyy-MM-dd}\n");

        var report = await ApiOk<ValueDateCostDto>(
            HttpMethod.Get, "/v1/ledger/value-date-cost", null, ("X-Api-Key", tenant.ApiKey));

        report.EarlyCount.ShouldBe(1);
        report.LateCount.ShouldBe(0);
        report.TotalCostMinor.ShouldBe(0); // erken ödeme "kazanç" olarak yazılmaz
    }

    // ---- F4: itiraz yaşam döngüsü ----------------------------------------------

    [Fact]
    public async Task Komisyon_itirazi_ACILIP_kapanisa_kadar_izlenmeli()
    {
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, rateBps: 250, valorDays: 1);

        await PayAsync(tenant.ApiKey, 1_000_00);
        await ProjectAsync();

        var receivable = (await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey))).ShouldHaveSingleItem();

        // Banka 6,00 ₺ FAZLA kesti
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        await UploadStatementAsync(tenant.ApiKey, accountId, day,
            "order_id;type;gross;commission;net;value_date\n"
            + $"{receivable.AttemptId};sale;100000;3100;96900;{day:yyyy-MM-dd}\n");

        // ① İtiraz açılır
        var claim = await ApiOk<ClaimDto>(HttpMethod.Post, "/v1/recon/claims", new
        {
            connectorAccountId = accountId,
            periodFrom = day.AddDays(-1),
            periodTo = day.AddDays(1),
        }, ("X-Api-Key", tenant.ApiKey));

        claim.Id.ShouldStartWith("clm_");
        claim.Status.ShouldBe("draft");
        claim.ClaimedMinor.ShouldBe(600);
        claim.FindingCount.ShouldBe(1);
        claim.OutstandingMinor.ShouldBe(600);

        // ② Bankaya iletilir
        var submitted = await ApiOk<ClaimDto>(
            HttpMethod.Post, $"/v1/recon/claims/{claim.Id}/advance",
            new { action = "submit", bankReference = "GRT-2026-77213", note = "Dönem itirazı" },
            ("X-Api-Key", tenant.ApiKey));
        submitted.Status.ShouldBe("submitted");
        submitted.BankReference.ShouldBe("GRT-2026-77213");
        submitted.SubmittedAt.ShouldNotBeNull();

        // ③ Banka KISMEN iade etti — itiraz KAPANMAZ
        var partial = await ApiOk<ClaimDto>(
            HttpMethod.Post, $"/v1/recon/claims/{claim.Id}/advance",
            new { action = "recover", amountMinor = 400 }, ("X-Api-Key", tenant.ApiKey));

        partial.Status.ShouldBe("partially_recovered");
        partial.RecoveredMinor.ShouldBe(400);
        partial.OutstandingMinor.ShouldBe(200); // kalanı unutmamak için AÇIK kalır
        partial.ClosedAt.ShouldBeNull();

        // ④ Kalan da gelince kapanır
        var closed = await ApiOk<ClaimDto>(
            HttpMethod.Post, $"/v1/recon/claims/{claim.Id}/advance",
            new { action = "recover", amountMinor = 200 }, ("X-Api-Key", tenant.ApiKey));

        closed.Status.ShouldBe("closed");
        closed.RecoveredMinor.ShouldBe(600);
        closed.OutstandingMinor.ShouldBe(0);
        closed.ClosedAt.ShouldNotBeNull();

        // ⑤ Zaman çizelgesi DEĞİŞTİRİLEMEZ ve her adımı taşır
        var timeline = await ApiOk<List<ClaimEventDto>>(
            HttpMethod.Get, $"/v1/recon/claims/{claim.Id}/timeline", null, ("X-Api-Key", tenant.ApiKey));

        timeline.Select(e => e.EventType).ShouldBe(new[] { "submit", "recover", "recover" });
        timeline.Where(e => e.AmountMinor is not null).Sum(e => e.AmountMinor!.Value).ShouldBe(600);

        // ⑥ Satış cümlesi: "bu yıl bankalardan geri alınan"
        var summary = await ApiOk<ClaimSummaryDto>(
            HttpMethod.Get, "/v1/recon/claims", null, ("X-Api-Key", tenant.ApiKey));
        summary.RecoveredThisYearMinor.ShouldBe(600);
        summary.OpenCount.ShouldBe(0);
        summary.ClosedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Reddedilen_itiraz_KAPANIR_ama_silinmez()
    {
        var (tenant, accountId) = await SeedAsync();
        await AgreeAsync(tenant.ApiKey, accountId, 250, 1);
        await PayAsync(tenant.ApiKey, 1_000_00);
        await ProjectAsync();

        var receivable = (await ApiOk<List<ReceivableDto>>(
            HttpMethod.Get, "/v1/receivables", null, ("X-Api-Key", tenant.ApiKey))).ShouldHaveSingleItem();
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        await UploadStatementAsync(tenant.ApiKey, accountId, day,
            "order_id;type;gross;commission;net;value_date\n"
            + $"{receivable.AttemptId};sale;100000;3100;96900;{day:yyyy-MM-dd}\n");

        var claim = await ApiOk<ClaimDto>(HttpMethod.Post, "/v1/recon/claims", new
        {
            connectorAccountId = accountId,
            periodFrom = day.AddDays(-1),
            periodTo = day.AddDays(1),
        }, ("X-Api-Key", tenant.ApiKey));

        var rejected = await ApiOk<ClaimDto>(
            HttpMethod.Post, $"/v1/recon/claims/{claim.Id}/advance",
            new { action = "reject", note = "Banka kampanya oranı uyguladığını bildirdi" },
            ("X-Api-Key", tenant.ApiKey));

        rejected.Status.ShouldBe("rejected");
        rejected.ClosedAt.ShouldNotBeNull();

        // Kayıt DURUYOR: aynı bankayla aynı konuda ikinci kez konuşurken
        // geçmiş cevabı bilmek gerekir
        var summary = await ApiOk<ClaimSummaryDto>(
            HttpMethod.Get, "/v1/recon/claims", null, ("X-Api-Key", tenant.ApiKey));
        summary.Claims.ShouldHaveSingleItem().Status.ShouldBe("rejected");

        var timeline = await ApiOk<List<ClaimEventDto>>(
            HttpMethod.Get, $"/v1/recon/claims/{claim.Id}/timeline", null, ("X-Api-Key", tenant.ApiKey));
        timeline.ShouldHaveSingleItem().Note.ShouldContain("kampanya");
    }

    [Fact]
    public async Task Bulgu_yoksa_itiraz_ACILMAMALI()
    {
        // Boş bir itiraz açmak, bankaya gerekçesiz gitmektir
        var (tenant, accountId) = await SeedAsync();
        var day = DateOnly.FromDateTime(DateTime.UtcNow);

        var response = await Send(HttpMethod.Post, "/v1/recon/claims", new
        {
            connectorAccountId = accountId,
            periodFrom = day,
            periodTo = day,
        }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("claim.no_findings");
    }

    // ================================================================== rol koruması

    private sealed record AuthTokensDto(string AccessToken);

    /// <summary>Verilen rolde kullanıcı açar (makine anahtarıyla — rolden muaf) ve JWT alır.</summary>
    private async Task<string> LoginWithRoleAsync(string apiKey, string role)
    {
        var email = $"{role}-{Guid.NewGuid():N}@ornek.com";
        await ApiOk<object>(HttpMethod.Post, "/v1/users",
            new { email, password = "rol-parola-1234", displayName = "Rol Testi", role },
            ("X-Api-Key", apiKey));

        return (await ApiOk<AuthTokensDto>(HttpMethod.Post, "/v1/auth/login",
            new { email, password = "rol-parola-1234" })).AccessToken;
    }

    private async Task<HttpResponseMessage> UploadAccountStatementAsAsync(
        Guid accountId, string headerName, string headerValue)
    {
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(
            "value_date;amount;description\n2026-08-14;100;POS HASILAT\n"));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        body.Add(file, "file", "hesap-ekstresi.csv");
        body.Add(new StringContent(accountId.ToString()), "connectorAccountId");
        body.Add(new StringContent("poyra_csv"), "format");

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/settlements/upload") { Content = body };
        request.Headers.Add(headerName, headerValue);
        return await _api.SendAsync(request);
    }

    [Fact]
    public async Task Denetci_hesap_ekstresi_YUKLEYEMEMELI_finans_yukleyebilmeli()
    {
        // Ekstre yüklemek defteri DEĞİŞTİRİR: yanlış ya da kötü niyetli bir dosya
        // sahte "eksik yatırma" bulgusu üretir. Salt okur denetçiye kapalı,
        // mutabakatın sahibi finansa açık — /v1/recon ile aynı taban.
        var (tenant, accountId) = await SeedAsync();

        var auditor = await LoginWithRoleAsync(tenant.ApiKey, "auditor");
        var denied = await UploadAccountStatementAsAsync(
            accountId, "Authorization", $"Bearer {auditor}");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).ShouldContain("insufficient_role");

        // Okuma denetçiye AÇIK kalır: koruma yalnız yazmaya uygulanır
        (await Send(HttpMethod.Get, "/v1/settlements/report", null,
            ("Authorization", $"Bearer {auditor}"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var finance = await LoginWithRoleAsync(tenant.ApiKey, "finance");
        var allowed = await UploadAccountStatementAsAsync(
            accountId, "Authorization", $"Bearer {finance}");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Valor_ayarini_denetci_DEGISTIREMEMELI_finans_degistirebilmeli()
    {
        // Finansman oranı bir finans sayısıdır: anlaşma oranını ve valörü de finans
        // tanımlar (/v1/recon/agreements). Eşik recon ile aynı — Finance; salt okur
        // denetçiye kapalı.
        var (tenant, _) = await SeedAsync();

        var auditor = await LoginWithRoleAsync(tenant.ApiKey, "auditor");
        var denied = await Send(HttpMethod.Post, "/v1/ledger/settings",
            new { annualFinancingRateBps = 3_500 }, ("Authorization", $"Bearer {auditor}"));
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).ShouldContain("insufficient_role");

        // Maliyet raporunu okumak en düşük ranka (denetçi) bile açık kalır
        (await Send(HttpMethod.Get, "/v1/ledger/value-date-cost", null,
            ("Authorization", $"Bearer {auditor}"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var finance = await LoginWithRoleAsync(tenant.ApiKey, "finance");
        var allowed = await Send(HttpMethod.Post, "/v1/ledger/settings",
            new { annualFinancingRateBps = 3_500 }, ("Authorization", $"Bearer {finance}"));
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }
}
