using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Recon.Domain;
using Poyra.Modules.Recon.Features;
using Poyra.Modules.Recon.Infrastructure;
using Poyra.SharedKernel.Tenancy;
using Shouldly;
using Xunit;
using Poyra.SharedKernel.Domain;

namespace Poyra.Tests.Integration;

/// <summary>
/// F2.2: CSV upload + valör denetimi + iade satırları (senkron yol) ve
/// büyük ekstrenin Hangfire'da asenkron eşleştirilmesi (ayrı fabrika, SyncMatchLimit=0).
/// </summary>
[Collection("postgres")]
public sealed class ReconDeepeningTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private readonly WebApplicationFactory<ApiEntryPoint> _syncFactory;
    private readonly WebApplicationFactory<ApiEntryPoint> _asyncFactory; // her ekstre Hangfire'a gider
    private readonly HttpClient _sync;
    private readonly HttpClient _async;

    public ReconDeepeningTests(PostgresFixture fixture)
    {
        WebApplicationFactory<ApiEntryPoint> Create(int syncLimit)
            => new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
                builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
                builder.UseSetting("Database:AutoMigrate", "false");
                builder.UseSetting("Platform:AdminKey", AdminKey);
                builder.UseSetting("Poyra:PublicBaseUrl", "http://localhost");
                builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
                builder.UseSetting("Poyra:JwtKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
                builder.UseSetting("Recon:SyncMatchLimit", syncLimit.ToString());
            });

        _syncFactory = Create(500);
        _asyncFactory = Create(0);
        _sync = _syncFactory.CreateClient();
        _async = _asyncFactory.CreateClient();
    }

    public void Dispose()
    {
        _syncFactory.Dispose();
        _asyncFactory.Dispose();
    }

    private sealed record TenantCreated(Guid TenantId, string ApiKey);
    private sealed record AccountDto(Guid Id);
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);
    private sealed record StatementSummary(
        Guid Id, string Status, int LineCount, int RefundLineCount, int MatchedCount,
        int MissingInPoyraCount, int MissingInStatementCount,
        long ExpectedCommissionSum, long CommissionDeltaSum, int AgreementMissingCount);
    private sealed record ExceptionLine(int LineNo, string OrderId, string LineType, string MatchStatus);
    private sealed record Exceptions(List<ExceptionLine> MissingInPoyra, List<ExceptionLine> AmountMismatch);
    private sealed record ValorLine(string OrderId, DateOnly? ValueDate, DateOnly ExpectedValueDate, int DeltaDays);
    private sealed record ValorReport(
        int AuditedCount, int OnTimeCount, int LateCount, int EarlyCount,
        int TotalLateDays, List<ValorLine> LateLines);

    private static DateOnly TrToday => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private static async Task<T> SendOk<T>(HttpClient client, HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<(TenantCreated Tenant, AccountDto Account)> SeedAsync(HttpClient client)
    {
        var tenant = await SendOk<TenantCreated>(client, HttpMethod.Post, "/v1/tenants",
            new { name = "Derin Mutabakat", slug = "derin-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));
        var account = await SendOk<AccountDto>(client, HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));
        return (tenant, account);
    }

    private static async Task<(string PaymentId, string OrderId)> RunCapturedPaymentAsync(
        HttpClient client, string apiKey, long amountMinor)
    {
        var payment = await SendOk<PaymentDto>(client, HttpMethod.Post, "/v1/payments",
            new { amountMinor, currency = "TRY", confirm = true }, ("X-Api-Key", apiKey));
        (await client.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields))).StatusCode.ShouldBe(HttpStatusCode.OK);
        // pay_… intent, att_… deneme kimliğidir — İKİSİ FARKLI uuid taşır
        return (payment.Id, payment.NextAction.Fields["mb_order"]);
    }

    [Fact]
    public async Task Csv_upload_valor_denetimi_ve_iade_satirlari()
    {
        var (tenant, account) = await SeedAsync(_sync);

        // Anlaşma: tek çekim %2,00, valör 2 gün
        await SendOk<object>(_sync, HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = account.Id, installmentCount = 1, rateBps = 200, valorDays = 2 },
            ("X-Api-Key", tenant.ApiKey));

        // Gerçek tahsilat + 4.000 kısmi iade
        var (paymentId, order) = await RunCapturedPaymentAsync(_sync, tenant.ApiKey, 10_000);
        await SendOk<object>(_sync, HttpMethod.Post, "/v1/refunds",
            new { paymentId, amountMinor = 4_000 }, ("X-Api-Key", tenant.ApiKey));

        // CSV: satış (valör 2 gün GEÇ beyanlı) + eşleşen iade + hayalet iade.
        // Beklenen valör İŞ GÜNÜ hesabıdır — üretimle aynı fonksiyonla türetilir.
        // Tatiller VARSAYILMAZ, okunur: bank_holidays platform tablosudur (RLS'siz) ve
        // aynı Postgres'i paylaşan başka bir test oraya tatil yazmış olabilir; sabit boş
        // küme kullanmak testi takvime bağımlı kılar (tatil valör penceresine düştüğü gün patlar).
        var seededHolidays = await SendOk<List<HolidayDto>>(_sync, HttpMethod.Get,
            $"/v1/bank-holidays?year={TrToday.Year}", null, ("X-Api-Key", tenant.ApiKey));
        var expectedValue = BusinessCalendar.AddBusinessDays(
            TrToday, 2, seededHolidays.Select(h => h.Day).ToHashSet());
        var lateValue = expectedValue.AddDays(2).ToString("yyyy-MM-dd"); // beyan 2 gün geç
        var csv = $"""
            order_id;type;gross_minor;commission_minor;net_minor;value_date
            {order};sale;10000;200;9800;{lateValue}
            {order};refund;4000;0;-4000;
            {order};refund;9999;0;-9999;
            """;

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(account.Id.ToString()), "connectorAccountId");
        form.Add(new StringContent(TrToday.ToString("yyyy-MM-dd")), "statementDate");
        form.Add(new StringContent("poyra_csv"), "format");
        form.Add(new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(csv))), "file", "gunsonu.csv");

        var uploadRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/recon/statements/upload")
        {
            Content = form,
        };
        uploadRequest.Headers.Add("X-Api-Key", tenant.ApiKey);
        var uploadResponse = await _sync.SendAsync(uploadRequest);
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.OK,
            await uploadResponse.Content.ReadAsStringAsync());
        var summary = (await uploadResponse.Content.ReadFromJsonAsync<StatementSummary>())!;

        // Eşleştirme: satış + gerçek iade eşleşti; hayalet iade defterde yok
        summary.Status.ShouldBe("matched");
        summary.LineCount.ShouldBe(3);
        summary.RefundLineCount.ShouldBe(2);
        summary.MatchedCount.ShouldBe(2);
        summary.MissingInPoyraCount.ShouldBe(1);
        summary.ExpectedCommissionSum.ShouldBe(200); // yalnız satış satırı denetlenir
        summary.CommissionDeltaSum.ShouldBe(0);

        var exceptions = await SendOk<Exceptions>(_sync, HttpMethod.Get,
            $"/v1/recon/statements/{summary.Id}/exceptions", null, ("X-Api-Key", tenant.ApiKey));
        var ghost = exceptions.MissingInPoyra.ShouldHaveSingleItem();
        ghost.LineType.ShouldBe("refund");
        ghost.MatchStatus.ShouldBe("missing_in_poyra");

        // Valör: banka 2 gün geç beyan etmiş
        var valor = await SendOk<ValorReport>(_sync, HttpMethod.Get,
            $"/v1/recon/statements/{summary.Id}/valor-report", null, ("X-Api-Key", tenant.ApiKey));
        valor.AuditedCount.ShouldBe(1);
        valor.LateCount.ShouldBe(1);
        valor.OnTimeCount.ShouldBe(0);
        valor.TotalLateDays.ShouldBe(2);
        var late = valor.LateLines.ShouldHaveSingleItem();
        late.OrderId.ShouldBe(order);
        late.DeltaDays.ShouldBe(2);
        late.ExpectedValueDate.ShouldBe(expectedValue);
    }

    [Fact]
    public async Task Bozuk_csv_satir_numarali_hatayla_reddedilmeli()
    {
        var (tenant, account) = await SeedAsync(_sync);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(account.Id.ToString()), "connectorAccountId");
        form.Add(new StringContent(TrToday.ToString("yyyy-MM-dd")), "statementDate");
        form.Add(new StreamContent(new MemoryStream("att_x;sale;bozuk;1;1"u8.ToArray())), "file", "k.csv");

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/recon/statements/upload")
        {
            Content = form,
        };
        request.Headers.Add("X-Api-Key", tenant.ApiKey);
        var response = await _sync.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("recon.parse_error");
        body.ShouldContain("satır 1");
    }

    [Fact]
    public async Task Buyuk_ekstre_hangfire_ile_asenkron_eslesmeli()
    {
        var (tenant, account) = await SeedAsync(_async); // SyncMatchLimit=0 → her ekstre asenkron

        await SendOk<object>(_async, HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = account.Id, installmentCount = 1, rateBps = 200, valorDays = 1 },
            ("X-Api-Key", tenant.ApiKey));
        var (_, order) = await RunCapturedPaymentAsync(_async, tenant.ApiKey, 10_000);

        var initial = await SendOk<StatementSummary>(_async, HttpMethod.Post, "/v1/recon/statements", new
        {
            connectorAccountId = account.Id,
            statementDate = TrToday,
            lines = new object[]
            {
                new { orderId = order, grossMinor = 10_000, commissionMinor = 300, netMinor = 9_700 },
            },
        }, ("X-Api-Key", tenant.ApiKey));

        // Yanıt anında döner: eşleştirme henüz koşmadı
        initial.Status.ShouldBe("matching");
        initial.MatchedCount.ShouldBe(0);

        // Hangfire işini bekle (Testing'de 1 sn polling)
        StatementSummary? final = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var list = await SendOk<List<StatementSummary>>(_async, HttpMethod.Get,
                "/v1/recon/statements", null, ("X-Api-Key", tenant.ApiKey));
            final = list.SingleOrDefault(s => s.Id == initial.Id);
            if (final?.Status == "matched")
                break;
            await Task.Delay(250);
        }

        final.ShouldNotBeNull();
        final.Status.ShouldBe("matched", "asenkron eşleştirme 20 sn içinde tamamlanmadı");
        final.MatchedCount.ShouldBe(1);
        final.ExpectedCommissionSum.ShouldBe(200);
        final.CommissionDeltaSum.ShouldBe(100); // 300 kesilmiş, 200 beklenen → +100 itiraz
    }

    [Fact]
    public async Task Banka_formati_is_gunu_valoru_ve_idempotent_eslesme()
    {
        var (tenant, account) = await SeedAsync(_sync);

        // Platform, ertesi günü banka tatili ilan eder — iş günü valörü bunu atlamalı
        var holiday = TrToday.AddDays(1);
        var holidayRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/bank-holidays")
        {
            Content = JsonContent.Create(new { holidays = new[] { new { day = holiday, name = "Test Tatili" } } }),
        };
        holidayRequest.Headers.Add("X-Platform-Key", AdminKey);
        (await _sync.SendAsync(holidayRequest)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await SendOk<object>(_sync, HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = account.Id, installmentCount = 1, rateBps = 200, valorDays = 1 },
            ("X-Api-Key", tenant.ApiKey));

        var (_, order) = await RunCapturedPaymentAsync(_sync, tenant.ApiKey, 10_000);

        // Beklenen valör, üretimle AYNI takvim fonksiyonuyla hesaplanır (tatil + hafta sonu atlanır)
        var expectedValor = BusinessCalendar.AddBusinessDays(TrToday, 1, new HashSet<DateOnly> { holiday });

        // NestPay gün sonu formatı: TR tutar biçimi + Türkçe işlem tipi
        var csv = $"""
            ORDER_ID;TRAN_TYPE;AMOUNT;COMMISSION;NET;VALOR
            {order};Satış;100,00;2,00;98,00;{expectedValor:dd.MM.yyyy}
            """;
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(account.Id.ToString()), "connectorAccountId");
        form.Add(new StringContent(TrToday.ToString("yyyy-MM-dd")), "statementDate");
        form.Add(new StringContent("nestpay_csv"), "format");
        form.Add(new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(csv))), "file", "nestpay.csv");
        var uploadRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/recon/statements/upload")
        {
            Content = form,
        };
        uploadRequest.Headers.Add("X-Api-Key", tenant.ApiKey);
        var uploadResponse = await _sync.SendAsync(uploadRequest);
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.OK,
            await uploadResponse.Content.ReadAsStringAsync());
        var summary = (await uploadResponse.Content.ReadFromJsonAsync<StatementSummary>())!;

        summary.MatchedCount.ShouldBe(1);
        summary.ExpectedCommissionSum.ShouldBe(200); // 100,00 TL × %2 (TR biçimi kuruşa doğru çevrildi)
        summary.CommissionDeltaSum.ShouldBe(0);

        // Valör: banka beyanı beklenen İŞ GÜNÜyle aynı → geç yok
        var valor = await SendOk<ValorReport>(_sync, HttpMethod.Get,
            $"/v1/recon/statements/{summary.Id}/valor-report", null, ("X-Api-Key", tenant.ApiKey));
        valor.AuditedCount.ShouldBe(1);
        valor.OnTimeCount.ShouldBe(1);
        valor.LateCount.ShouldBe(0);

        // İdempotentlik: eşleştirmeyi elle İKİNCİ kez koş (süpürme senaryosu) —
        // sayaçlar ve toplamlar değişmemeli, bulgular çiftlenmemeli (unique index + skip)
        using (var scope = _syncFactory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant.TenantId);
            await scope.ServiceProvider.GetRequiredService<StatementMatcher>()
                .MatchAsync(summary.Id, default);
        }

        var list = await SendOk<List<StatementSummary>>(_sync, HttpMethod.Get,
            "/v1/recon/statements", null, ("X-Api-Key", tenant.ApiKey));
        var after = list.Single(s => s.Id == summary.Id);
        after.MatchedCount.ShouldBe(1);
        after.ExpectedCommissionSum.ShouldBe(200); // çiftlenseydi 400 olurdu
        after.Status.ShouldBe("matched");
    }
}
