using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F2.1 ana farklılaştırıcı: gerçek ödemeler (mock akışı) → ekstre içe aktarma →
/// iki yönlü eşleştirme + istisnalar → komisyon denetimi ("bankanın kestiğini kuruşuna
/// kadar doğrula"). Vade farklı işlemde brüt = ÇEKİLEN tutar olarak denetlenir.
/// </summary>
[Collection("postgres")]
public sealed class ReconFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _client;

    public ReconFlowTests(PostgresFixture fixture)
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

    private sealed record TenantCreated(Guid TenantId, string ApiKey);
    private sealed record AccountDto(Guid Id, string Label);
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, long? ChargedAmountMinor, NextAction? NextAction);
    private sealed record StatementSummary(
        Guid Id, DateOnly StatementDate, string Status, int LineCount, int MatchedCount,
        int MissingInPoyraCount, int AmountMismatchCount, int MissingInStatementCount,
        long ExpectedCommissionSum, long ActualCommissionSum, long CommissionDeltaSum,
        int AgreementMissingCount);
    private sealed record ExceptionLine(int LineNo, string OrderId, long GrossMinor, string MatchStatus);
    private sealed record Exceptions(
        Guid StatementId, List<ExceptionLine> MissingInPoyra, List<ExceptionLine> AmountMismatch,
        int MissingInStatementCount);
    private sealed record Finding(
        string OrderId, int Installments, long GrossMinor, int? AgreedRateBps,
        long? ExpectedCommissionMinor, long ActualCommissionMinor, long? DeltaMinor);
    private sealed record CommissionReport(
        Guid StatementId, long ExpectedCommissionSum, long ActualCommissionSum, long DeltaSum,
        int OverchargedCount, int UnderchargedCount, int AgreementMissingCount, List<Finding> Discrepancies);

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

    private async Task<PaymentDto> RunCapturedPaymentAsync(
        string apiKey, long amountMinor, int installments = 1)
    {
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor, currency = "TRY", installments, confirm = true }, ("X-Api-Key", apiKey));
        payment.Status.ShouldBe("requires_action");
        (await _client.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields))).StatusCode.ShouldBe(HttpStatusCode.OK);
        return payment;
    }

    // Ekstre satırındaki sipariş no = bankaya giden oid = att_… (deneme dış kimliği)
    private static string OrderIdOf(NextAction action) => action.Fields["mb_order"];

    [Fact]
    public async Task Ekstre_eslesme_istisnalar_ve_komisyon_denetimi_ucta_uca()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Mutabakat", slug = "recon-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));
        var account = await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts",
            new
            {
                connectorKey = "mockbank",
                label = "Mock POS",
                credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
                priority = 1,
            }, ("X-Api-Key", tenant.ApiKey));

        // 3 taksit şeması (%10 vade farkı) — köprü çekilen tutarı 22.000 yapar
        await SendOk<object>(HttpMethod.Post, "/v1/installments/schemes",
            new { connectorAccountId = account.Id, program = "*", installmentCount = 3, customerRateBps = 1_000 },
            ("X-Api-Key", tenant.ApiKey));

        // Anlaşmalar: tek çekim %2,00 · 3 taksit %3,50
        await SendOk<object>(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = account.Id, installmentCount = 1, rateBps = 200, valorDays = 1 },
            ("X-Api-Key", tenant.ApiKey));
        await SendOk<object>(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = account.Id, installmentCount = 3, rateBps = 350, valorDays = 1 },
            ("X-Api-Key", tenant.ApiKey));

        // Üç gerçek tahsilat: p1 tek çekim 10.000 · p2 3 taksit 20.000→çekilen 22.000 · p3 ekstrede unutulacak
        var p1 = await RunCapturedPaymentAsync(tenant.ApiKey, 10_000);
        var p2 = await RunCapturedPaymentAsync(tenant.ApiKey, 20_000, installments: 3);
        p2.ChargedAmountMinor.ShouldBe(22_000);
        var p3 = await RunCapturedPaymentAsync(tenant.ApiKey, 5_000);

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

        // Ekstre: p1 komisyonu FAZLA kesilmiş (250, beklenen 200) · p2 doğru (770) ·
        // hayalet satır (defterde yok) · p3 ekstrede YOK (bankada kayıp)
        var summary = await SendOk<StatementSummary>(HttpMethod.Post, "/v1/recon/statements", new
        {
            connectorAccountId = account.Id,
            statementDate = today,
            lines = new object[]
            {
                new { orderId = OrderIdOf(p1.NextAction!), grossMinor = 10_000, commissionMinor = 250, netMinor = 9_750 },
                new { orderId = OrderIdOf(p2.NextAction!), grossMinor = 22_000, commissionMinor = 770, netMinor = 21_230 },
                new { orderId = "att_hayalet000", grossMinor = 7_777, commissionMinor = 100, netMinor = 7_677 },
            },
        }, ("X-Api-Key", tenant.ApiKey));

        // Eşleştirme özeti
        summary.LineCount.ShouldBe(3);
        summary.MatchedCount.ShouldBe(2);
        summary.MissingInPoyraCount.ShouldBe(1); // hayalet
        summary.AmountMismatchCount.ShouldBe(0);
        summary.MissingInStatementCount.ShouldBe(1); // p3
        summary.Status.ShouldBe("matched");

        // Komisyon denetimi: beklenen 200+770=970, kesilen 250+770=1020, fark +50
        summary.ExpectedCommissionSum.ShouldBe(970);
        summary.ActualCommissionSum.ShouldBe(1_020);
        summary.CommissionDeltaSum.ShouldBe(50);
        summary.AgreementMissingCount.ShouldBe(0);

        // İstisna ekranı
        var exceptions = await SendOk<Exceptions>(HttpMethod.Get,
            $"/v1/recon/statements/{summary.Id}/exceptions", null, ("X-Api-Key", tenant.ApiKey));
        exceptions.MissingInPoyra.ShouldHaveSingleItem().OrderId.ShouldBe("att_hayalet000");
        exceptions.AmountMismatch.ShouldBeEmpty();
        exceptions.MissingInStatementCount.ShouldBe(1);

        // "Bankaya İtiraz Raporu": tek uyuşmazlık p1, +50 kuruş fazla kesim
        var report = await SendOk<CommissionReport>(HttpMethod.Get,
            $"/v1/recon/statements/{summary.Id}/commission-report", null, ("X-Api-Key", tenant.ApiKey));
        report.DeltaSum.ShouldBe(50);
        report.OverchargedCount.ShouldBe(1);
        report.UnderchargedCount.ShouldBe(0);
        var discrepancy = report.Discrepancies.ShouldHaveSingleItem();
        discrepancy.OrderId.ShouldBe(OrderIdOf(p1.NextAction!));
        discrepancy.ExpectedCommissionMinor.ShouldBe(200);
        discrepancy.ActualCommissionMinor.ShouldBe(250);
        discrepancy.DeltaMinor.ShouldBe(50);
        discrepancy.AgreedRateBps.ShouldBe(200);

        // Liste ucu da aynı toplamları göstermeli
        var list = await SendOk<List<StatementSummary>>(HttpMethod.Get, "/v1/recon/statements", null,
            ("X-Api-Key", tenant.ApiKey));
        list.Single(s => s.Id == summary.Id).CommissionDeltaSum.ShouldBe(50);
    }

    [Fact]
    public async Task Tutar_uyusmazligi_ve_anlasmasiz_taksit_ayri_sayilmali()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Mutabakat2", slug = "recon2-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));
        var account = await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts",
            new
            {
                connectorKey = "mockbank",
                label = "Mock POS",
                credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
                priority = 1,
            }, ("X-Api-Key", tenant.ApiKey));
        // Bilerek HİÇ anlaşma girilmedi

        var p1 = await RunCapturedPaymentAsync(tenant.ApiKey, 10_000);
        var p2 = await RunCapturedPaymentAsync(tenant.ApiKey, 8_000);

        var summary = await SendOk<StatementSummary>(HttpMethod.Post, "/v1/recon/statements", new
        {
            connectorAccountId = account.Id,
            statementDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)),
            lines = new object[]
            {
                // p1 brütü yanlış yazılmış → amount_mismatch (komisyon denetimine girmez)
                new { orderId = OrderIdOf(p1.NextAction!), grossMinor = 9_999, commissionMinor = 200, netMinor = 9_799 },
                // p2 eşleşir ama anlaşma yok → agreement_missing (sıfır farkla KARIŞMAZ)
                new { orderId = OrderIdOf(p2.NextAction!), grossMinor = 8_000, commissionMinor = 300, netMinor = 7_700 },
            },
        }, ("X-Api-Key", tenant.ApiKey));

        summary.AmountMismatchCount.ShouldBe(1);
        summary.MatchedCount.ShouldBe(1);
        summary.AgreementMissingCount.ShouldBe(1);
        summary.ExpectedCommissionSum.ShouldBe(0); // denetlenebilen satır yok
        summary.CommissionDeltaSum.ShouldBe(0);

        var report = await SendOk<CommissionReport>(HttpMethod.Get,
            $"/v1/recon/statements/{summary.Id}/commission-report", null, ("X-Api-Key", tenant.ApiKey));
        report.AgreementMissingCount.ShouldBe(1);
        report.Discrepancies.ShouldBeEmpty();
    }

    [Fact]
    public async Task Mutabakat_yazmasi_finance_rolu_ister()
    {
        var email = $"denetci-{Guid.NewGuid():N}@ornek.com";
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "RolRecon", slug = "rolrecon-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));
        await SendOk<object>(HttpMethod.Post, "/v1/users",
            new { email, password = "denetci-parola-1", displayName = "Denetçi", role = "auditor" },
            ("X-Api-Key", tenant.ApiKey));
        var login = await SendOk<Dictionary<string, object>>(HttpMethod.Post, "/v1/auth/login",
            new { email, password = "denetci-parola-1" });

        var denied = await Send(HttpMethod.Post, "/v1/recon/agreements",
            new { connectorAccountId = Guid.NewGuid(), installmentCount = 1, rateBps = 100 },
            ("Authorization", $"Bearer {login["accessToken"]}"));
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden); // auditor(10) < finance(40)
    }
}
