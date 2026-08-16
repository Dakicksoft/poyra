using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Poyra.Api;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// M08 Müşteriler ve talimatlar. İki soru test edilir:
/// <b>görüntü eksiksiz mi</b> (bugün beş ayrı yerde duran cevap tek ekranda mı) ve
/// <b>KVKK silme doğru mu</b> (kişisel veri gidiyor, mali kayıt kalıyor).
/// </summary>
[Collection("postgres")]
public sealed class CustomerFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string TestVisa = "4355084355084358";

    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _api;

    public CustomerFlowTests(PostgresFixture fixture)
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
        _api = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string ApiKey);
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);
    private sealed record CardDto(string Token, string MaskedPan);
    private sealed record PlanDto(string Id, string Name);
    private sealed record SubscriptionDto(string Id, string Status);
    private sealed record CustomerDto(
        string Ref, string? Name, string? Email, string? Phone, string? Notes,
        bool Erased, DateTimeOffset? ErasedAt, DateTimeOffset CreatedAt);
    private sealed record MandateDto(
        string Id, string CustomerRef, string CardToken, string Type, string TextVersion,
        DateTimeOffset AcceptedAt, string? AcceptedIp, bool Active,
        DateTimeOffset? RevokedAt, string? RevokedReason);
    private sealed record TotalsDto(int Count, long SucceededMinor, long RefundedMinor);
    private sealed record CustomerPaymentDto(
        string PaymentId, long AmountMinor, string Currency, string Status,
        int Installments, string? MaskedPan, DateTimeOffset CreatedAt);
    private sealed record CustomerCardDto(
        string Token, string MaskedPan, string Brand, int ExpiryMonth, int ExpiryYear, bool Removed);
    private sealed record CustomerSubscriptionDto(
        string SubscriptionId, string PlanName, long AmountMinor, string Status,
        DateTimeOffset CurrentPeriodEnd, bool NeedsCardUpdate);
    private sealed record DetailDto(
        CustomerDto Customer, TotalsDto Totals, List<CustomerPaymentDto> Payments,
        List<CustomerCardDto> Cards, List<CustomerSubscriptionDto> Subscriptions, List<MandateDto> Mandates);

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
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "Müşteri A.Ş.", slug = "mus-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));

        await SendOk<object>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        return tenant;
    }

    private async Task<string> PaidPaymentAsync(string apiKey, string customerRef, long amountMinor)
    {
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor, currency = "TRY", customerRef, confirm = true }, ("X-Api-Key", apiKey));

        (await _api.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields))).StatusCode.ShouldBe(HttpStatusCode.OK);

        return payment.Id;
    }

    private Task<CardDto> SaveCardAsync(string apiKey, string customerRef)
        => SendOk<CardDto>(HttpMethod.Post, "/v1/vault/cards", new
        {
            cardNumber = TestVisa,
            expiryMonth = 12,
            expiryYear = DateTime.UtcNow.Year + 3,
            holderName = "AYSE YILMAZ",
            customerRef,
        }, ("X-Api-Key", apiKey));

    // ---- Kayıt --------------------------------------------------------------------

    [Fact]
    public async Task Musteri_kaydedilip_guncellenebilmeli()
    {
        var tenant = await SeedTenantAsync();

        var created = await SendOk<CustomerDto>(HttpMethod.Put, "/v1/customers/cust-1",
            new { name = "Ayşe Yılmaz", email = "AYSE@Ornek.COM", phone = "0532 123 45 67" },
            ("X-Api-Key", tenant.ApiKey));

        created.Ref.ShouldBe("cust-1");
        created.Name.ShouldBe("Ayşe Yılmaz");
        created.Email.ShouldBe("ayse@ornek.com"); // küçük harfe normalleşir
        created.Phone.ShouldBe("+905321234567"); // E.164'e normalleşir — SMS biçime takılmasın

        var updated = await SendOk<CustomerDto>(HttpMethod.Put, "/v1/customers/cust-1",
            new { name = "Ayşe Yılmaz", notes = "VIP" }, ("X-Api-Key", tenant.ApiKey));

        updated.Notes.ShouldBe("VIP");

        // Aynı referans iki müşteri açmamalı: geçmiş ikiye bölünür ve hiçbiri tam görünmez
        (await SendOk<List<CustomerDto>>(HttpMethod.Get, "/v1/customers", null,
            ("X-Api-Key", tenant.ApiKey))).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Gecersiz_telefon_reddedilmeli()
    {
        var tenant = await SeedTenantAsync();

        var response = await Send(HttpMethod.Put, "/v1/customers/cust-tel",
            new { phone = "0212 123 45 67" }, ("X-Api-Key", tenant.ApiKey)); // sabit hat

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Musteri_aranabilmeli()
    {
        var tenant = await SeedTenantAsync();
        await SendOk<CustomerDto>(HttpMethod.Put, "/v1/customers/alfa-tekstil",
            new { name = "Alfa Tekstil", email = "alfa@ornek.com" }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<CustomerDto>(HttpMethod.Put, "/v1/customers/beta-lojistik",
            new { name = "Beta Lojistik" }, ("X-Api-Key", tenant.ApiKey));

        var byRef = await SendOk<List<CustomerDto>>(
            HttpMethod.Get, "/v1/customers?search=alfa", null, ("X-Api-Key", tenant.ApiKey));
        byRef.ShouldHaveSingleItem().Ref.ShouldBe("alfa-tekstil");

        // Büyük/küçük harf farkı aramayı bozmamalı
        var byName = await SendOk<List<CustomerDto>>(
            HttpMethod.Get, "/v1/customers?search=BETA", null, ("X-Api-Key", tenant.ApiKey));
        byName.ShouldHaveSingleItem().Ref.ShouldBe("beta-lojistik");
    }

    // ---- Tek görünüm ---------------------------------------------------------------

    [Fact]
    public async Task Musteri_gorunumu_odemeleri_kartlari_ve_abonelikleri_toplamali()
    {
        var tenant = await SeedTenantAsync();
        const string customerRef = "cust-tam";

        await SendOk<CustomerDto>(HttpMethod.Put, $"/v1/customers/{customerRef}",
            new { name = "Tam Görünüm", email = "tam@ornek.com" }, ("X-Api-Key", tenant.ApiKey));

        await PaidPaymentAsync(tenant.ApiKey, customerRef, 50_000);
        await PaidPaymentAsync(tenant.ApiKey, customerRef, 30_000);

        var card = await SaveCardAsync(tenant.ApiKey, customerRef);

        var plan = await SendOk<PlanDto>(HttpMethod.Post, "/v1/plans",
            new { name = "Aylık", amountMinor = 20_000 }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<SubscriptionDto>(HttpMethod.Post, "/v1/subscriptions",
            new { planId = plan.Id, customerRef, cardToken = card.Token }, ("X-Api-Key", tenant.ApiKey));

        var detail = await SendOk<DetailDto>(HttpMethod.Get, $"/v1/customers/{customerRef}", null,
            ("X-Api-Key", tenant.ApiKey));

        detail.Customer.Name.ShouldBe("Tam Görünüm");
        detail.Payments.Count.ShouldBeGreaterThanOrEqualTo(2);
        detail.Cards.ShouldHaveSingleItem().MaskedPan.ShouldBe("435508******4358");
        detail.Subscriptions.ShouldHaveSingleItem().PlanName.ShouldBe("Aylık");

        // Abonelik ilk dönemi hemen tahsil eder → toplam 3 ödeme, 100.000 kuruş
        detail.Totals.Count.ShouldBe(3);
        detail.Totals.SucceededMinor.ShouldBe(100_000);
    }

    [Fact]
    public async Task Kaydi_olmayan_musterinin_odemeleri_de_gorunmeli()
    {
        var tenant = await SeedTenantAsync();
        await PaidPaymentAsync(tenant.ApiKey, "kayitsiz", 12_000);

        // "Müşteri kaydı yok" demek, var olan ödemeleri görünmez kılardı —
        // customerRef ödeme akışından gelir, kayıt sonradan açılabilir
        var detail = await SendOk<DetailDto>(HttpMethod.Get, "/v1/customers/kayitsiz", null,
            ("X-Api-Key", tenant.ApiKey));

        detail.Customer.Ref.ShouldBe("kayitsiz");
        detail.Customer.Name.ShouldBeNull();
        detail.Payments.ShouldHaveSingleItem().AmountMinor.ShouldBe(12_000);
        detail.Totals.SucceededMinor.ShouldBe(12_000);
    }

    [Fact]
    public async Task Musteri_gorunumu_duz_PAN_SIZDIRMAMALI()
    {
        var tenant = await SeedTenantAsync();
        await SaveCardAsync(tenant.ApiKey, "cust-pci");

        var response = await Send(HttpMethod.Get, "/v1/customers/cust-pci", null,
            ("X-Api-Key", tenant.ApiKey));
        var body = await response.Content.ReadAsStringAsync();

        // Müşteri ekranı PCI kapsamına girmemelidir
        body.ShouldNotContain(TestVisa);
        body.ShouldContain("435508******4358");
    }

    // ---- KVKK silme -------------------------------------------------------------------

    [Fact]
    public async Task KVKK_silme_kisisel_veriyi_silmeli_MALI_KAYDI_BIRAKMALI()
    {
        var tenant = await SeedTenantAsync();
        const string customerRef = "cust-kvkk";

        await SendOk<CustomerDto>(HttpMethod.Put, $"/v1/customers/{customerRef}",
            new { name = "Silinecek Kişi", email = "silinecek@ornek.com", phone = "05329998877", notes = "not" },
            ("X-Api-Key", tenant.ApiKey));
        var paymentId = await PaidPaymentAsync(tenant.ApiKey, customerRef, 45_000);

        var erased = await SendOk<CustomerDto>(HttpMethod.Post, $"/v1/customers/{customerRef}/erase",
            null, ("X-Api-Key", tenant.ApiKey));

        // ① Kişisel veri gitti
        erased.Erased.ShouldBeTrue();
        erased.Name.ShouldBeNull();
        erased.Email.ShouldBeNull();
        erased.Phone.ShouldBeNull();
        erased.Notes.ShouldBeNull();

        // ② Mali kayıt DURUYOR — silme hakkı, VUK/TTK saklama yükümlülüğünü kaldırmaz
        var detail = await SendOk<DetailDto>(HttpMethod.Get, $"/v1/customers/{customerRef}", null,
            ("X-Api-Key", tenant.ApiKey));
        detail.Payments.ShouldHaveSingleItem().PaymentId.ShouldBe(paymentId);
        detail.Totals.SucceededMinor.ShouldBe(45_000);

        // ③ Ödeme kaydının kendisi de bozulmadı
        (await Send(HttpMethod.Get, $"/v1/payments/{paymentId}", null, ("X-Api-Key", tenant.ApiKey)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // ④ Veritabanında da isim kalmadı
        await using var db = _fixture.CreateCustomers(PostgresFixture.TenantCtx(tenant.TenantId));
        var row = await db.Customers.AsNoTracking().SingleAsync(c => c.Ref == customerRef);
        row.Name.ShouldBeNull();
        row.Email.ShouldBeNull();
        row.Ref.ShouldBe(customerRef); // referans KORUNUR: mali defter ona bağlı
    }

    [Fact]
    public async Task Silinen_musteri_yeniden_veri_girisine_KAPALI_olmali()
    {
        var tenant = await SeedTenantAsync();
        await SendOk<CustomerDto>(HttpMethod.Put, "/v1/customers/cust-geri",
            new { name = "Kişi" }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<CustomerDto>(HttpMethod.Post, "/v1/customers/cust-geri/erase", null,
            ("X-Api-Key", tenant.ApiKey));

        // Yeniden veri girmek silme talebini geçersiz kılardı
        var response = await Send(HttpMethod.Put, "/v1/customers/cust-geri",
            new { name = "Yeniden Kişi" }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("customer.erased");
    }

    [Fact]
    public async Task Iki_kez_silinememeli()
    {
        var tenant = await SeedTenantAsync();
        await SendOk<CustomerDto>(HttpMethod.Put, "/v1/customers/cust-iki",
            new { name = "Kişi" }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<CustomerDto>(HttpMethod.Post, "/v1/customers/cust-iki/erase", null,
            ("X-Api-Key", tenant.ApiKey));

        var again = await Send(HttpMethod.Post, "/v1/customers/cust-iki/erase", null,
            ("X-Api-Key", tenant.ApiKey));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task KVKK_silme_acik_talimatlari_iptal_etmeli()
    {
        var tenant = await SeedTenantAsync();
        const string customerRef = "cust-talimat";
        var card = await SaveCardAsync(tenant.ApiKey, customerRef);

        await SendOk<CustomerDto>(HttpMethod.Put, $"/v1/customers/{customerRef}",
            new { name = "Talimatlı" }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<MandateDto>(HttpMethod.Post, $"/v1/customers/{customerRef}/mandates",
            new { cardToken = card.Token, textVersion = "v1" }, ("X-Api-Key", tenant.ApiKey));

        await SendOk<CustomerDto>(HttpMethod.Post, $"/v1/customers/{customerRef}/erase", null,
            ("X-Api-Key", tenant.ApiKey));

        // Kişisel verisi silinen müşteriden çekim sürmemeli
        var detail = await SendOk<DetailDto>(HttpMethod.Get, $"/v1/customers/{customerRef}", null,
            ("X-Api-Key", tenant.ApiKey));
        var mandate = detail.Mandates.ShouldHaveSingleItem();
        mandate.Active.ShouldBeFalse();
        mandate.RevokedReason.ShouldContain("KVKK");
    }

    [Fact]
    public async Task KVKK_silme_denetim_defterine_dusmeli()
    {
        var tenant = await SeedTenantAsync();
        await SendOk<CustomerDto>(HttpMethod.Put, "/v1/customers/cust-iz",
            new { name = "İzli" }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<CustomerDto>(HttpMethod.Post, "/v1/customers/cust-iz/erase", null,
            ("X-Api-Key", tenant.ApiKey));

        var audit = await SendOk<List<Dictionary<string, System.Text.Json.JsonElement>>>(
            HttpMethod.Get, "/v1/compliance/audit?resourceType=customer", null, ("X-Api-Key", tenant.ApiKey));

        // "Kimin verisi ne zaman, kim tarafından silindi" KVKK'nın sorduğu sorudur.
        // Eylem adı ALT EYLEMDEN türemeli: "customer.created" diye kaydedilseydi
        // uyum görevlisi eyleme göre filtrelediğinde silmeyi hiç bulamazdı.
        audit.ShouldContain(e => e["action"].GetString() == "customer.erase");
        audit.ShouldContain(e => e["summary"].GetString()!.Contains("/erase"));
    }

    // ---- Talimat --------------------------------------------------------------------------

    [Fact]
    public async Task Talimat_kaydedilip_iptal_edilebilmeli()
    {
        var tenant = await SeedTenantAsync();
        var card = await SaveCardAsync(tenant.ApiKey, "cust-mnd");

        // IP'yi İŞYERİ gönderir: istek onun sunucusundan geldiği için otomatik
        // yakalanan adres müşteriyi değil altyapıyı gösterir ve savunmada işe yaramaz
        var mandate = await SendOk<MandateDto>(HttpMethod.Post, "/v1/customers/cust-mnd/mandates",
            new
            {
                cardToken = card.Token,
                textVersion = "sozlesme-2026-01",
                type = "recurring",
                acceptedIp = "203.0.113.45",
            }, ("X-Api-Key", tenant.ApiKey));

        mandate.Id.ShouldStartWith("mnd_");
        mandate.Active.ShouldBeTrue();
        mandate.TextVersion.ShouldBe("sozlesme-2026-01");
        mandate.AcceptedIp.ShouldBe("203.0.113.45"); // savunmada "nereden" sorulur

        var revoked = await SendOk<MandateDto>(HttpMethod.Post, $"/v1/mandates/{mandate.Id}/revoke",
            new { reason = "Müşteri aradı." }, ("X-Api-Key", tenant.ApiKey));

        revoked.Active.ShouldBeFalse();
        revoked.RevokedReason.ShouldBe("Müşteri aradı.");

        // Kayıt DURUYOR: iptalden önceki çekimlerin dayanağı sonradan sorulur
        var detail = await SendOk<DetailDto>(HttpMethod.Get, "/v1/customers/cust-mnd", null,
            ("X-Api-Key", tenant.ApiKey));
        detail.Mandates.ShouldHaveSingleItem().Id.ShouldBe(mandate.Id);
    }

    [Fact]
    public async Task Ayni_kart_icin_iki_aktif_talimat_olmamali()
    {
        var tenant = await SeedTenantAsync();
        var card = await SaveCardAsync(tenant.ApiKey, "cust-cift");

        await SendOk<MandateDto>(HttpMethod.Post, "/v1/customers/cust-cift/mandates",
            new { cardToken = card.Token, textVersion = "v1" }, ("X-Api-Key", tenant.ApiKey));

        // İki aktif onay, hangisinin dayanak olduğunu belirsiz bırakır
        var again = await Send(HttpMethod.Post, "/v1/customers/cust-cift/mandates",
            new { cardToken = card.Token, textVersion = "v2" }, ("X-Api-Key", tenant.ApiKey));

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).ShouldContain("mandate.already_active");
    }

    [Fact]
    public async Task Iptalden_sonra_yeni_talimat_alinabilmeli()
    {
        var tenant = await SeedTenantAsync();
        var card = await SaveCardAsync(tenant.ApiKey, "cust-yeni");

        var first = await SendOk<MandateDto>(HttpMethod.Post, "/v1/customers/cust-yeni/mandates",
            new { cardToken = card.Token, textVersion = "v1" }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<MandateDto>(HttpMethod.Post, $"/v1/mandates/{first.Id}/revoke",
            new { reason = "Metin güncellendi." }, ("X-Api-Key", tenant.ApiKey));

        // Sözleşme metni değişince yeni onay alınır — kısmi indeks buna izin vermeli
        var second = await SendOk<MandateDto>(HttpMethod.Post, "/v1/customers/cust-yeni/mandates",
            new { cardToken = card.Token, textVersion = "v2" }, ("X-Api-Key", tenant.ApiKey));

        second.Active.ShouldBeTrue();
        second.TextVersion.ShouldBe("v2");
    }

    [Fact]
    public async Task Metin_surumsuz_talimat_reddedilmeli()
    {
        var tenant = await SeedTenantAsync();
        var card = await SaveCardAsync(tenant.ApiKey, "cust-surumsuz");

        // Sürüm olmadan "müşteri neyi kabul etti" sorusu cevapsız kalır
        var response = await Send(HttpMethod.Post, "/v1/customers/cust-surumsuz/mandates",
            new { cardToken = card.Token, textVersion = "" }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---- Yalıtım ve defter -----------------------------------------------------------------

    [Fact]
    public async Task Baska_isyerinin_musterisi_gorunmemeli()
    {
        var first = await SeedTenantAsync();
        var second = await SeedTenantAsync();

        await SendOk<CustomerDto>(HttpMethod.Put, "/v1/customers/ortak-ref",
            new { name = "İlk işyerinin müşterisi", email = "gizli@ornek.com" },
            ("X-Api-Key", first.ApiKey));
        await PaidPaymentAsync(first.ApiKey, "ortak-ref", 77_000);

        // Aynı referans ikinci işyerinde BOŞ görünmeli — referanslar işyerine özeldir
        var detail = await SendOk<DetailDto>(HttpMethod.Get, "/v1/customers/ortak-ref", null,
            ("X-Api-Key", second.ApiKey));

        detail.Customer.Name.ShouldBeNull();
        detail.Payments.ShouldBeEmpty();
        detail.Totals.SucceededMinor.ShouldBe(0);

        (await SendOk<List<CustomerDto>>(HttpMethod.Get, "/v1/customers", null,
            ("X-Api-Key", second.ApiKey))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Musteri_ve_talimat_silinemez_olmali()
    {
        var tenant = await SeedTenantAsync();
        var card = await SaveCardAsync(tenant.ApiKey, "cust-silinmez");
        await SendOk<CustomerDto>(HttpMethod.Put, "/v1/customers/cust-silinmez",
            new { name = "Kalıcı" }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<MandateDto>(HttpMethod.Post, "/v1/customers/cust-silinmez/mandates",
            new { cardToken = card.Token, textVersion = "v1" }, ("X-Api-Key", tenant.ApiKey));

        await using var db = _fixture.CreateCustomers(PostgresFixture.TenantCtx(tenant.TenantId));

        // Müşteri kaydını silmek mali defteri koparır — KVKK silme bir UPDATE'tir
        db.Customers.RemoveRange(await db.Customers.ToListAsync());
        var customerDelete = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        customerDelete.InnerException.ShouldBeOfType<Npgsql.PostgresException>().SqlState.ShouldBe("42501");

        db.ChangeTracker.Clear();
        db.Mandates.RemoveRange(await db.Mandates.ToListAsync());
        var mandateDelete = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        mandateDelete.InnerException.ShouldBeOfType<Npgsql.PostgresException>().SqlState.ShouldBe("42501");
    }
}
