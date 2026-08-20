using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Subscriptions;
using Poyra.Modules.Subscriptions.Domain;
using Poyra.Modules.Subscriptions.Infrastructure;
using Poyra.SharedKernel.Tenancy;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F3.1: abonelik + akıllı dunning. Kasa token'ıyla tekrarlayan tahsilat, dönem ilerletme,
/// başarısızlıkta hata koduna göre dunning kararı ve kart güncellemesiyle kurtarma.
/// </summary>
[Collection("postgres")]
public sealed class SubscriptionFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string TestVisa = "4111111111111111";
    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _client;

    public SubscriptionFlowTests(PostgresFixture fixture)
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
    private sealed record AccountDto(Guid Id);
    private sealed record CardTokenDto(string Token, string MaskedPan);
    private sealed record PlanDto(string Id, string Name, long AmountMinor, string Interval, int TrialDays);
    private sealed record SubscriptionDto(
        string Id, string PlanId, string CustomerRef, string Status, string CardToken,
        DateTimeOffset CurrentPeriodStart, DateTimeOffset CurrentPeriodEnd,
        bool CancelAtPeriodEnd, bool NeedsCardUpdate);
    private sealed record InvoiceDto(
        string Id, string SubscriptionId, long AmountMinor, string Status, int AttemptCount,
        DateTimeOffset? NextRetryAt, string? LastPaymentId, string? LastErrorCode, DateTimeOffset? PaidAt);

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

    private async Task<(TenantCreated Tenant, string CardToken)> SeedAsync()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Abonelik", slug = "abo-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));
        await SendOk<AccountDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        var card = await SendOk<CardTokenDto>(HttpMethod.Post, "/v1/vault/cards",
            new { cardNumber = TestVisa, expiryMonth = 12, expiryYear = 2031, customerRef = "musteri-1" },
            ("X-Api-Key", tenant.ApiKey));

        return (tenant, card.Token);
    }

    private async Task<PlanDto> CreatePlanAsync(string apiKey, long amountMinor, int trialDays = 0)
        => await SendOk<PlanDto>(HttpMethod.Post, "/v1/plans",
            new { name = "Aylık Paket", amountMinor, currency = "TRY", interval = "month", trialDays },
            ("X-Api-Key", apiKey));

    [Fact]
    public async Task Abonelik_baslar_ilk_donem_tahsil_edilir_ve_donem_ilerler()
    {
        var (tenant, cardToken) = await SeedAsync();
        var plan = await CreatePlanAsync(tenant.ApiKey, 15_000);

        var subscription = await SendOk<SubscriptionDto>(HttpMethod.Post, "/v1/subscriptions",
            new { planId = plan.Id, customerRef = "musteri-1", cardToken },
            ("X-Api-Key", tenant.ApiKey));

        subscription.Id.ShouldStartWith("sub_");
        subscription.Status.ShouldBe("active");

        // İlk dönem hemen tahsil edildi (token ile 3DS'siz direct)
        var invoices = await SendOk<List<InvoiceDto>>(HttpMethod.Get,
            $"/v1/subscription-invoices?subscriptionId={subscription.Id}", null, ("X-Api-Key", tenant.ApiKey));
        var first = invoices.ShouldHaveSingleItem();
        first.Status.ShouldBe("paid");
        first.AmountMinor.ShouldBe(15_000);
        first.AttemptCount.ShouldBe(1);
        first.LastPaymentId.ShouldStartWith("pay_"); // normal ödeme defterinde görünür

        // Abonelik tahsilatı rotaya "subscription" kanalıyla girer: müşteri ekranda değildir,
        // kart token'dan gelir. Rota "yenilemeleri en yüksek başarılı POS'a" diyebilsin diye
        // bu kanalın API ödemesinden ayrılması şart.
        await using (var db = _fixture.CreatePayments(PostgresFixture.TenantCtx(tenant.TenantId)))
        {
            var intent = await db.PaymentIntents.AsNoTracking()
                .SingleAsync(i => i.PublicId == first.LastPaymentId);
            intent.Channel.ShouldBe("subscription");
        }

        // Dönem sonunu geçmişe çekip tahakkuk işini koş → yeni dönem faturalanır
        await ShiftPeriodEndToPastAsync(tenant.TenantId, subscription.Id);
        await RunBillingAsync(tenant.TenantId);

        var afterBilling = await SendOk<List<InvoiceDto>>(HttpMethod.Get,
            $"/v1/subscription-invoices?subscriptionId={subscription.Id}", null, ("X-Api-Key", tenant.ApiKey));
        afterBilling.Count.ShouldBe(2);
        afterBilling.ShouldAllBe(i => i.Status == "paid");

        var refreshed = (await SendOk<List<SubscriptionDto>>(HttpMethod.Get, "/v1/subscriptions", null,
            ("X-Api-Key", tenant.ApiKey))).Single();
        refreshed.CurrentPeriodEnd.ShouldBeGreaterThan(DateTimeOffset.UtcNow); // dönem ilerledi
        refreshed.Status.ShouldBe("active");

        // Tahakkuk işi ikinci kez koşsa bile aynı dönem tekrar faturalanmaz
        await RunBillingAsync(tenant.TenantId);
        (await SendOk<List<InvoiceDto>>(HttpMethod.Get,
            $"/v1/subscription-invoices?subscriptionId={subscription.Id}", null,
            ("X-Api-Key", tenant.ApiKey))).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Kart_reddinde_dunning_baslar_kart_guncellemesiyle_kurtarilir()
    {
        var (tenant, cardToken) = await SeedAsync();
        // MockBank: kuruş %100 == 99 → kart reddi (poyra.card_declined)
        var plan = await CreatePlanAsync(tenant.ApiKey, 10_099);

        var subscription = await SendOk<SubscriptionDto>(HttpMethod.Post, "/v1/subscriptions",
            new { planId = plan.Id, customerRef = "musteri-1", cardToken },
            ("X-Api-Key", tenant.ApiKey));

        subscription.Status.ShouldBe("past_due"); // ilk tahsilat düştü → dunning

        var invoice = (await SendOk<List<InvoiceDto>>(HttpMethod.Get,
            $"/v1/subscription-invoices?subscriptionId={subscription.Id}", null,
            ("X-Api-Key", tenant.ApiKey))).ShouldHaveSingleItem();
        invoice.Status.ShouldBe("retrying");
        invoice.LastErrorCode.ShouldBe("poyra.card_declined");
        invoice.AttemptCount.ShouldBe(1);
        invoice.NextRetryAt.ShouldNotBeNull(); // 1 gün sonra (üstel geri çekilme)

        // Müşteri kartını günceller → bekleyen fatura HEMEN kuyruğa alınır…
        var newCard = await SendOk<CardTokenDto>(HttpMethod.Post, "/v1/vault/cards",
            new { cardNumber = "5555555555554444", expiryMonth = 6, expiryYear = 2032, customerRef = "musteri-2" },
            ("X-Api-Key", tenant.ApiKey));
        await SendOk<SubscriptionDto>(HttpMethod.Post, $"/v1/subscriptions/{subscription.Id}/card",
            new { cardToken = newCard.Token }, ("X-Api-Key", tenant.ApiKey));

        // …ama tutar hâlâ reddedilen tutar; planı düzeltip dunning turunu koş
        await SetPlanAmountAsync(tenant.TenantId, plan.Id, 12_000);
        await SetInvoiceAmountAsync(tenant.TenantId, invoice.Id, 12_000);
        await RunDunningAsync(tenant.TenantId);

        var recovered = (await SendOk<List<InvoiceDto>>(HttpMethod.Get,
            $"/v1/subscription-invoices?subscriptionId={subscription.Id}", null,
            ("X-Api-Key", tenant.ApiKey))).ShouldHaveSingleItem();
        recovered.Status.ShouldBe("paid");
        recovered.AttemptCount.ShouldBe(2); // ikinci denemede kurtarıldı

        var refreshed = (await SendOk<List<SubscriptionDto>>(HttpMethod.Get, "/v1/subscriptions", null,
            ("X-Api-Key", tenant.ApiKey))).Single();
        refreshed.Status.ShouldBe("active"); // past_due → active
        refreshed.NeedsCardUpdate.ShouldBeFalse();
    }

    [Fact]
    public async Task Deneme_suresi_ve_iptal_akislari()
    {
        var (tenant, cardToken) = await SeedAsync();
        var trialPlan = await CreatePlanAsync(tenant.ApiKey, 20_000, trialDays: 14);

        var subscription = await SendOk<SubscriptionDto>(HttpMethod.Post, "/v1/subscriptions",
            new { planId = trialPlan.Id, customerRef = "musteri-1", cardToken },
            ("X-Api-Key", tenant.ApiKey));

        subscription.Status.ShouldBe("trialing");
        subscription.CurrentPeriodEnd.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(13));

        // Denemede fatura kesilmez
        (await SendOk<List<InvoiceDto>>(HttpMethod.Get,
            $"/v1/subscription-invoices?subscriptionId={subscription.Id}", null,
            ("X-Api-Key", tenant.ApiKey))).ShouldBeEmpty();

        // Dönem sonunda iptal: hemen kapanmaz, bayrak konur
        var cancelled = await SendOk<SubscriptionDto>(HttpMethod.Post,
            $"/v1/subscriptions/{subscription.Id}/cancel", new { atPeriodEnd = true },
            ("X-Api-Key", tenant.ApiKey));
        cancelled.CancelAtPeriodEnd.ShouldBeTrue();
        cancelled.Status.ShouldBe("trialing");

        // Deneme bitince tahakkuk işi tahsil ETMEDEN kapatır
        await ShiftPeriodEndToPastAsync(tenant.TenantId, subscription.Id);
        await RunBillingAsync(tenant.TenantId);

        var final = (await SendOk<List<SubscriptionDto>>(HttpMethod.Get, "/v1/subscriptions", null,
            ("X-Api-Key", tenant.ApiKey))).Single();
        final.Status.ShouldBe("cancelled");
        (await SendOk<List<InvoiceDto>>(HttpMethod.Get,
            $"/v1/subscription-invoices?subscriptionId={subscription.Id}", null,
            ("X-Api-Key", tenant.ApiKey))).ShouldBeEmpty(); // hiç tahsilat yapılmadı
    }

    /// <summary>
    /// Buraya kadarki testler <see cref="SubscriptionBiller"/>'ı işyeri bağlamını KENDİ
    /// kurup çağırıyor. Üretimde onu çağıran şey Hangfire işidir ve işin kendine ait bir
    /// sorumluluğu var: aktif işyerlerini gezmek. Kapsam ölçümü o döngünün %0 olduğunu
    /// gösterdi — yani "iş koştu ama yalnız ilk işyerini faturaladı" hatası sessiz kalırdı.
    /// </summary>
    [Fact]
    public async Task Tahakkuk_ISI_butun_isyerlerini_faturalamali()
    {
        var (isyeriA, kartA) = await SeedAsync();
        var (isyeriB, kartB) = await SeedAsync();

        var aboA = await AbonelikAcAsync(isyeriA, kartA, 10_000);
        var aboB = await AbonelikAcAsync(isyeriB, kartB, 20_000);

        await ShiftPeriodEndToPastAsync(isyeriA.TenantId, aboA);
        await ShiftPeriodEndToPastAsync(isyeriB.TenantId, aboB);

        await RunBillingJobAsync();

        // İkisi de ikinci dönemini almalı: döngü ilk işyerinde dursaydı B'nin aboneliği
        // sessizce tahsil edilmez ve işyeri parasını hiç alamazdı.
        (await FaturalarAsync(isyeriA, aboA)).Count.ShouldBe(2);
        (await FaturalarAsync(isyeriB, aboB)).Count.ShouldBe(2);

        // Dunning işi de işyeri döngüsüdür; ödenmiş faturaya dokunmamalı
        await RunDunningJobAsync();
        (await FaturalarAsync(isyeriA, aboA)).ShouldAllBe(i => i.Status == "paid");
        (await FaturalarAsync(isyeriB, aboB)).ShouldAllBe(i => i.Status == "paid");
    }

    private async Task<string> AbonelikAcAsync(TenantCreated tenant, string cardToken, long amountMinor)
    {
        var plan = await CreatePlanAsync(tenant.ApiKey, amountMinor);
        var subscription = await SendOk<SubscriptionDto>(HttpMethod.Post, "/v1/subscriptions",
            new { planId = plan.Id, customerRef = "musteri-1", cardToken },
            ("X-Api-Key", tenant.ApiKey));
        return subscription.Id;
    }

    private Task<List<InvoiceDto>> FaturalarAsync(TenantCreated tenant, string subscriptionId)
        => SendOk<List<InvoiceDto>>(HttpMethod.Get,
            $"/v1/subscription-invoices?subscriptionId={subscriptionId}", null,
            ("X-Api-Key", tenant.ApiKey));

    /// <summary>
    /// İş PLATFORM bağlamında başlar ve işyerlerini kendisi gezer — testin işyeri seçmemesi
    /// bilinçli. (Aynı veritabanındaki başka işyerlerinin vadesi gelmiş abonelikleri de
    /// faturalanır; koleksiyon içi testler sıralı koştuğu için bu sorun çıkarmıyor.)
    /// </summary>
    private async Task RunBillingJobAsync()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();
        await scope.ServiceProvider
            .GetRequiredService<Poyra.Modules.Subscriptions.Infrastructure.SubscriptionBillingJob>()
            .BillAllAsync();
    }

    private async Task RunDunningJobAsync()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();
        await scope.ServiceProvider
            .GetRequiredService<Poyra.Modules.Subscriptions.Infrastructure.DunningRetryJob>()
            .RetryAllAsync();
    }

    // ---- Test düzeneği: zamanı ileri sarmak yerine kayıtları geçmişe çekeriz --------
    private async Task ShiftPeriodEndToPastAsync(Guid tenantId, string subscriptionPublicId)
    {
        await using var db = CreateSubscriptions(tenantId);
        var subscription = await db.Subscriptions.SingleAsync(s => s.PublicId == subscriptionPublicId);
        subscription.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private async Task SetPlanAmountAsync(Guid tenantId, string planPublicId, long amountMinor)
    {
        await using var db = CreateSubscriptions(tenantId);
        var plan = await db.Plans.SingleAsync(p => p.PublicId == planPublicId);
        plan.AmountMinor = amountMinor;
        await db.SaveChangesAsync();
    }

    private async Task SetInvoiceAmountAsync(Guid tenantId, string invoicePublicId, long amountMinor)
    {
        // AmountMinor init-only: ham SQL ile düzeltilir (yalnız test düzeneği)
        await using var db = CreateSubscriptions(tenantId);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE subscription_invoices SET amount_minor = {amountMinor} WHERE public_id = {invoicePublicId}");
    }

    private SubscriptionsDbContext CreateSubscriptions(Guid tenantId)
    {
        var tenant = PostgresFixture.TenantCtx(tenantId);
        return new SubscriptionsDbContext(
            Poyra.Persistence.PoyraDb.BuildOptions<SubscriptionsDbContext>(
                _fixture.AppCs, SubscriptionsDbContext.MigrationsHistoryTable, tenant,
                new Poyra.SharedKernel.Time.SystemClock()),
            tenant);
    }

    private async Task RunBillingAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
        await scope.ServiceProvider.GetRequiredService<SubscriptionBiller>().BillDueSubscriptionsAsync();
    }

    private async Task RunDunningAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
        await scope.ServiceProvider.GetRequiredService<SubscriptionBiller>().RetryDueInvoicesAsync();
    }
}
