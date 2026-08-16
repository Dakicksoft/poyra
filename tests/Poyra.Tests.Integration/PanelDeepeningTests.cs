using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Poyra.Api;
using Poyra.Panel;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F7 pekiştirme: API'de olup panelde olmayan yüzeyler kapatıldı — webhook,
/// ekip/anahtar ve abonelik ekranları. "API'den yapılabiliyor" cevabı, panele
/// giren işyeri sahibi için bir cevap değildir.
/// </summary>
[Collection("postgres")]
public sealed class PanelDeepeningTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string Password = "panel-derin-1234";

    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory;
    private readonly WebApplicationFactory<PanelEntryPoint> _panelFactory;
    private readonly HttpClient _api;

    public PanelDeepeningTests(PostgresFixture fixture)
    {
        void Configure(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Panel:Antiforgery:Enforce", "false"); // form token kurulumu ayrı testte
            builder.UseSetting("Platform:AdminKey", AdminKey);
            builder.UseSetting("Poyra:PublicBaseUrl", "http://localhost");
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Poyra:JwtKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            builder.UseSetting("Poyra:VaultKey", Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
        }

        _apiFactory = new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(Configure);
        _panelFactory = new WebApplicationFactory<PanelEntryPoint>().WithWebHostBuilder(Configure);
        _api = _apiFactory.CreateClient();
    }

    public void Dispose()
    {
        _apiFactory.Dispose();
        _panelFactory.Dispose();
    }

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record ApiKeyRow(Guid Id, string Name, string PrefixHint, bool Revoked);
    private sealed record WebhookEndpointRow(Guid Id, string Url, string[] EventTypes, bool Active, string? Secret);
    private sealed record PlanRow(
        string Id, string Name, long AmountMinor, string Currency,
        string Interval, int IntervalCount, int TrialDays, bool Active);
    private sealed record SubscriptionRow(
        string Id, string PlanId, string CustomerRef, string Status, string CardToken,
        DateTimeOffset CurrentPeriodStart, DateTimeOffset CurrentPeriodEnd,
        bool CancelAtPeriodEnd, bool NeedsCardUpdate);
    private sealed record CardRow(string Token);
    private sealed record UserRow(Guid UserId, string Email, string Role);

    private HttpClient CreatePanelClient()
        => _panelFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpResponseMessage> ApiSend(HttpMethod method, string path, object? body,
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
        var response = await ApiSend(method, path, body, headers);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(TenantCreated Tenant, string OwnerEmail)> SeedAsync()
    {
        var email = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await ApiOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Derin A.Ş.",
            slug = "drn-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = Password,
            ownerName = "Sahip",
        }, ("X-Platform-Key", AdminKey));

        await ApiOk<object>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        return (tenant, email);
    }

    private async Task<string> AddUserAsync(string apiKey, string role)
    {
        var email = $"uye-{Guid.NewGuid():N}@ornek.com";
        await ApiOk<object>(HttpMethod.Post, "/v1/users",
            new { email, password = Password, displayName = "Üye", role }, ("X-Api-Key", apiKey));
        return email;
    }

    private static async Task<HttpClient> LoginAsync(HttpClient panel, string email, string slug)
    {
        var response = await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = Password,
            ["tenantSlug"] = slug,
        }));
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return panel;
    }

    private static string Location(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return Uri.UnescapeDataString(response.Headers.Location!.ToString());
    }

    // ---- Webhook ekranı ----------------------------------------------------------

    [Fact]
    public async Task Webhook_ucu_panelden_eklenebilmeli_ve_sir_BIR_KEZ_gosterilmeli()
    {
        var (tenant, email) = await SeedAsync();
        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);

        var page = await panel.GetStringAsync("/webhooklar");
        page.ShouldContain("Webhook ucu yok");
        page.ShouldContain("payment.succeeded"); // olay kataloğu modülden okunuyor
        page.ShouldContain("subscription.card_update_required");

        var created = await panel.PostAsync("/webhooklar/ekle",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["url"] = "https://sisteminiz.example/poyra",
                ["eventTypes"] = "payment.succeeded",
            }));

        // Sır URL'e YAZILMAZ (geçmiş/log sızıntısı) — tek kullanımlık taşıyıcı + çerezle taşınır
        var location = Location(created);
        location.ShouldContain("sonuc=");
        location.ShouldNotContain("whsec_");

        // İlk görüntülemede sır sayfada BİR KEZ görünür…
        var afterAdd = await panel.GetStringAsync(location);
        PanelRedirects.AlertText(afterAdd).ShouldContain("eklendi");
        afterAdd.ShouldContain("whsec_");

        // …ikinci görüntülemede artık görünmez — "bir kez" iddiası gerçek
        var again = await panel.GetStringAsync("/webhooklar");
        again.ShouldContain("sisteminiz.example");
        again.ShouldNotContain("whsec_");
    }

    [Fact]
    public async Task Webhook_sirri_panelden_dondurulebilmeli()
    {
        var (tenant, email) = await SeedAsync();
        var endpoint = await ApiOk<WebhookEndpointRow>(HttpMethod.Post, "/v1/webhook-endpoints",
            new { url = "https://sisteminiz.example/poyra", eventTypes = new[] { "payment.succeeded" } },
            ("X-Api-Key", tenant.ApiKey));
        var firstSecret = endpoint.Secret.ShouldNotBeNull();

        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);
        var rotated = await panel.PostAsync($"/webhooklar/{endpoint.Id}/sir", new FormUrlEncodedContent([]));

        var location = Location(rotated);
        location.ShouldNotContain("whsec_"); // sır URL'de taşınmaz

        var afterRotate = await panel.GetStringAsync(location);
        PanelRedirects.AlertText(afterRotate).ShouldContain("Sır yenilendi");
        afterRotate.ShouldContain("whsec_");
        afterRotate.ShouldNotContain(firstSecret); // gerçekten YENİ sır
    }

    [Fact]
    public async Task Webhook_ucu_panelden_kapatilip_acilabilmeli()
    {
        var (tenant, email) = await SeedAsync();
        var endpoint = await ApiOk<WebhookEndpointRow>(HttpMethod.Post, "/v1/webhook-endpoints",
            new { url = "https://sisteminiz.example/poyra", eventTypes = new[] { "payment.succeeded" } },
            ("X-Api-Key", tenant.ApiKey));

        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/webhooklar/{endpoint.Id}/durum",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["active"] = "false" }))))
            .ShouldContain("kapatıldı");

        (await panel.GetStringAsync("/webhooklar")).ShouldContain("Kapalı");

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/webhooklar/{endpoint.Id}/durum",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["active"] = "true" }))))
            .ShouldContain("açıldı");
    }

    [Fact]
    public async Task Webhook_teslimati_panelden_yeniden_gonderilebilmeli()
    {
        var (tenant, email) = await SeedAsync();
        await ApiOk<WebhookEndpointRow>(HttpMethod.Post, "/v1/webhook-endpoints",
            new { url = "http://127.0.0.1:1/olmayan", eventTypes = new[] { "payment.succeeded" } },
            ("X-Api-Key", tenant.ApiKey));

        // Gerçek bir ödeme akışı teslimat üretir
        var payment = await ApiOk<PaymentWithAction>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 25_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        (await _api.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var deliveries = await WaitForDeliveriesAsync(tenant.ApiKey);
        deliveries.Count.ShouldBeGreaterThan(0, "ödeme başarılı oldu, teslimat açılmalıydı");

        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);
        var page = await panel.GetStringAsync("/webhooklar");
        page.ShouldContain("payment.succeeded");
        page.ShouldContain("Yeniden gönder");

        var replayed = await panel.PostAsync($"/webhooklar/teslimat/{deliveries[0].Id}/yeniden",
            new FormUrlEncodedContent([]));
        (await PanelRedirects.RevealAsync(panel, replayed)).ShouldContain("yeniden kuyruğa alındı");

        // Yeniden gönderim ESKİYİ DEĞİŞTİRMEZ, yeni kayıt açar (İlke 3)
        var after = await ApiOk<List<DeliveryRow>>(HttpMethod.Get, "/v1/webhook-deliveries", null,
            ("X-Api-Key", tenant.ApiKey));
        after.Count.ShouldBe(deliveries.Count + 1);
        after.ShouldContain(d => d.ReplayOfId == deliveries[0].Id);
    }

    private sealed record DeliveryRow(Guid Id, string EventType, string Status, Guid? ReplayOfId);
    private sealed record NextActionRow(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentWithAction(string Id, string Status, NextActionRow? NextAction);

    private async Task<List<DeliveryRow>> WaitForDeliveriesAsync(string apiKey)
    {
        // Outbox dağıtıcısı arka planda koşar; kısa bir pencere tanınır
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var rows = await ApiOk<List<DeliveryRow>>(
                HttpMethod.Get, "/v1/webhook-deliveries", null, ("X-Api-Key", apiKey));
            if (rows.Count > 0)
                return rows;
            await Task.Delay(200);
        }

        return [];
    }

    [Fact]
    public async Task Denetci_webhook_yonetememeli()
    {
        var (tenant, _) = await SeedAsync();
        var endpoint = await ApiOk<WebhookEndpointRow>(HttpMethod.Post, "/v1/webhook-endpoints",
            new { url = "https://sisteminiz.example/poyra", eventTypes = new[] { "payment.succeeded" } },
            ("X-Api-Key", tenant.ApiKey));

        var auditorEmail = await AddUserAsync(tenant.ApiKey, "auditor");
        var panel = await LoginAsync(CreatePanelClient(), auditorEmail, tenant.Slug);

        // Arayüzde düğme yok…
        var page = await panel.GetStringAsync("/webhooklar");
        page.ShouldContain("sisteminiz.example"); // okuma serbest
        page.ShouldNotContain("Sırrı yenile");
        page.ShouldNotContain("Yeni uç nokta");

        // …ve doğrudan POST edilse bile sunucu reddeder (ikinci kapı)
        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/webhooklar/{endpoint.Id}/sir",
                new FormUrlEncodedContent([]))))
            .ShouldContain("'developer' rolü gerekli");
    }

    // ---- Ekip ve anahtar ekranı --------------------------------------------------

    [Fact]
    public async Task Ekip_ekrani_kullanicilari_ve_anahtarlari_gostermeli()
    {
        var (tenant, ownerEmail) = await SeedAsync();
        var memberEmail = await AddUserAsync(tenant.ApiKey, "operations");

        var panel = await LoginAsync(CreatePanelClient(), ownerEmail, tenant.Slug);
        var page = await panel.GetStringAsync("/ekip");

        page.ShouldContain(ownerEmail);
        page.ShouldContain(memberEmail);
        page.ShouldContain("(siz)"); // kendi satırının rol kutusu kilitli
        page.ShouldContain("Anahtar üret");
        page.ShouldNotContain(tenant.ApiKey); // düz anahtar ekranda ASLA görünmez
    }

    [Fact]
    public async Task Panelden_anahtar_uretilip_iptal_edilebilmeli()
    {
        var (tenant, email) = await SeedAsync();
        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);

        var created = await panel.PostAsync("/ekip/anahtar",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Üretim sunucusu" }));
        var location = Location(created);
        location.ShouldNotContain("sk_test_"); // anahtar URL'de taşınmaz

        var page = await panel.GetStringAsync(location);
        var match = System.Text.RegularExpressions.Regex.Match(page, @"sk_test_[A-Za-z0-9_\-]+");
        match.Success.ShouldBeTrue("anahtar ilk görüntülemede sayfada görünmeli");

        // Üretilen anahtar GERÇEKTEN çalışmalı — sadece ekranda göstermek yetmez
        var plain = match.Value;

        // İkinci görüntülemede TAM anahtar artık görünmez (önek sütunu kalır)
        (await panel.GetStringAsync("/ekip")).ShouldNotContain(plain);
        (await ApiSend(HttpMethod.Get, "/v1/users", null, ("X-Api-Key", plain)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var keys = await ApiOk<List<ApiKeyRow>>(HttpMethod.Get, "/v1/api-keys", null,
            ("X-Api-Key", tenant.ApiKey));
        var old = keys.Single(k => k.Name != "Üretim sunucusu");

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync(
                $"/ekip/anahtar/{old.Id}/iptal", new FormUrlEncodedContent([]))))
            .ShouldContain("iptal edildi");

        (await ApiSend(HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Panelden_son_aktif_anahtar_iptal_edilememeli()
    {
        var (tenant, email) = await SeedAsync();
        var keys = await ApiOk<List<ApiKeyRow>>(HttpMethod.Get, "/v1/api-keys", null,
            ("X-Api-Key", tenant.ApiKey));

        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);
        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync(
                $"/ekip/anahtar/{keys[0].Id}/iptal", new FormUrlEncodedContent([]))))
            .ShouldContain("Son aktif anahtar iptal edilemez");

        // Anahtar hâlâ çalışıyor — panel işyerini kilitlemedi
        (await ApiSend(HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Panelden_kullanici_eklenip_rolu_degistirilip_kaldirilabilmeli()
    {
        var (tenant, email) = await SeedAsync();
        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);

        var newEmail = $"yeni-{Guid.NewGuid():N}@ornek.com";
        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync("/ekip/ekle", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = newEmail,
            ["displayName"] = "Yeni Üye",
            ["password"] = Password,
            ["role"] = "operations",
        })))).ShouldContain("eklendi");

        var users = await ApiOk<List<UserRow>>(HttpMethod.Get, "/v1/users", null,
            ("X-Api-Key", tenant.ApiKey));
        var added = users.Single(u => u.Email == newEmail);
        added.Role.ShouldBe("operations");

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/ekip/{added.UserId}/rol",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["role"] = "finance" }))))
            .ShouldContain("artık 'finance'");

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/ekip/{added.UserId}/kaldir", new FormUrlEncodedContent([]))))
            .ShouldContain("Erişim kaldırıldı");

        (await ApiOk<List<UserRow>>(HttpMethod.Get, "/v1/users", null, ("X-Api-Key", tenant.ApiKey)))
            .ShouldNotContain(u => u.Email == newEmail);
    }

    [Fact]
    public async Task Admin_anahtar_yonetememeli_ama_kullanici_yonetebilmeli()
    {
        var (tenant, _) = await SeedAsync();
        var adminEmail = await AddUserAsync(tenant.ApiKey, "admin");
        var panel = await LoginAsync(CreatePanelClient(), adminEmail, tenant.Slug);

        var page = await panel.GetStringAsync("/ekip");
        page.ShouldContain("Kullanıcı ekle"); // admin ekip yönetir
        page.ShouldNotContain("Anahtar üret"); // ama anahtar sahibin işi

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync("/ekip/anahtar",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Yetkisiz" }))))
            .ShouldContain("'owner' rolü gerekli");

        // Gerçekten üretilmedi
        (await ApiOk<List<ApiKeyRow>>(HttpMethod.Get, "/v1/api-keys", null, ("X-Api-Key", tenant.ApiKey)))
            .Count.ShouldBe(1);
    }

    // ---- Abonelik ekranları ------------------------------------------------------

    [Fact]
    public async Task Plan_panelden_TR_yazimiyla_olusturulabilmeli()
    {
        var (tenant, email) = await SeedAsync();
        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);

        (await panel.GetStringAsync("/abonelikler")).ShouldContain("Plan yok");

        // "299,90" TR yazımı — [FromForm] bağlayıcısı bunu 400'e çevirirdi
        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync("/abonelikler/plan", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["name"] = "Kurumsal Aylık",
                ["amountLira"] = "299,90",
                ["interval"] = "month",
                ["intervalCount"] = "1",
                ["trialDays"] = "14",
            })))).ShouldContain("planı hazır");

        var plans = await ApiOk<List<PlanRow>>(HttpMethod.Get, "/v1/plans", null,
            ("X-Api-Key", tenant.ApiKey));
        var plan = plans.ShouldHaveSingleItem();
        plan.AmountMinor.ShouldBe(29_990); // kuruş asla kaybolmaz
        plan.TrialDays.ShouldBe(14);

        var page = await panel.GetStringAsync("/abonelikler");
        page.ShouldContain("Kurumsal Aylık");
        page.ShouldContain("299,90 ₺");
        page.ShouldContain("14 gün");
    }

    [Fact]
    public async Task Kapatilan_plana_yeni_abonelik_alinmamali_ama_mevcut_abone_ETKILENMEMELI()
    {
        var (tenant, email) = await SeedAsync();
        var plan = await ApiOk<PlanRow>(HttpMethod.Post, "/v1/plans",
            new { name = "Eski Tarife", amountMinor = 10_000 }, ("X-Api-Key", tenant.ApiKey));
        var card = await ApiOk<CardRow>(HttpMethod.Post, "/v1/vault/cards", new
        {
            cardNumber = "4355084355084358",
            expiryMonth = 12,
            expiryYear = DateTime.UtcNow.Year + 3,
            holderName = "ABONE MUSTERI",
        }, ("X-Api-Key", tenant.ApiKey));

        var existing = await ApiOk<SubscriptionRow>(HttpMethod.Post, "/v1/subscriptions",
            new { planId = plan.Id, customerRef = "cust-eski", cardToken = card.Token },
            ("X-Api-Key", tenant.ApiKey));

        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);
        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/abonelikler/plan/{plan.Id}/durum",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["active"] = "false" }))))
            .ShouldContain("kapatıldı");

        // ★ Yeni abonelik alınmaz…
        var rejected = await ApiSend(HttpMethod.Post, "/v1/subscriptions",
            new { planId = plan.Id, customerRef = "cust-yeni", cardToken = card.Token },
            ("X-Api-Key", tenant.ApiKey));
        rejected.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // …ama mevcut abone kendi fiyatından devam eder: zam, aboneyi sessizce
        // pahalıya bindirmekle değil, yeni plana taşımakla yapılır
        var subscriptions = await ApiOk<List<SubscriptionRow>>(HttpMethod.Get, "/v1/subscriptions", null,
            ("X-Api-Key", tenant.ApiKey));
        subscriptions.Single(s => s.Id == existing.Id).Status.ShouldBe("active");
    }

    [Fact]
    public async Task Abonelik_detayi_fatura_gecmisini_ve_islemleri_gostermeli()
    {
        var (tenant, email) = await SeedAsync();
        var plan = await ApiOk<PlanRow>(HttpMethod.Post, "/v1/plans",
            new { name = "Aylık Paket", amountMinor = 45_000 }, ("X-Api-Key", tenant.ApiKey));
        var card = await ApiOk<CardRow>(HttpMethod.Post, "/v1/vault/cards", new
        {
            cardNumber = "4355084355084358",
            expiryMonth = 12,
            expiryYear = DateTime.UtcNow.Year + 3,
            holderName = "ABONE MUSTERI",
        }, ("X-Api-Key", tenant.ApiKey));

        var subscription = await ApiOk<SubscriptionRow>(HttpMethod.Post, "/v1/subscriptions",
            new { planId = plan.Id, customerRef = "cust-1024", cardToken = card.Token },
            ("X-Api-Key", tenant.ApiKey));

        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);
        var detail = await panel.GetStringAsync($"/abonelikler/{subscription.Id}");

        detail.ShouldContain("cust-1024");
        detail.ShouldContain("Aylık Paket");
        detail.ShouldContain("450,00 ₺"); // ilk dönem tahsil edildi
        detail.ShouldContain("Ödendi");
        detail.ShouldContain("Kartı güncelle");
        detail.ShouldContain("Hemen iptal");
    }

    [Fact]
    public async Task Abonelik_panelden_donem_sonunda_ve_hemen_iptal_edilebilmeli()
    {
        var (tenant, email) = await SeedAsync();
        var plan = await ApiOk<PlanRow>(HttpMethod.Post, "/v1/plans",
            new { name = "Aylık", amountMinor = 20_000 }, ("X-Api-Key", tenant.ApiKey));
        var card = await ApiOk<CardRow>(HttpMethod.Post, "/v1/vault/cards", new
        {
            cardNumber = "4355084355084358",
            expiryMonth = 12,
            expiryYear = DateTime.UtcNow.Year + 3,
            holderName = "ABONE MUSTERI",
        }, ("X-Api-Key", tenant.ApiKey));

        var subscription = await ApiOk<SubscriptionRow>(HttpMethod.Post, "/v1/subscriptions",
            new { planId = plan.Id, customerRef = "cust-iptal", cardToken = card.Token },
            ("X-Api-Key", tenant.ApiKey));

        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/abonelikler/{subscription.Id}/iptal",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["atPeriodEnd"] = "true" }))))
            .ShouldContain("Dönem sonunda kapanacak");

        var afterSoft = await ApiOk<List<SubscriptionRow>>(HttpMethod.Get, "/v1/subscriptions", null,
            ("X-Api-Key", tenant.ApiKey));
        var soft = afterSoft.Single(s => s.Id == subscription.Id);
        soft.CancelAtPeriodEnd.ShouldBeTrue();
        soft.Status.ShouldBe("active"); // müşteri ödediği süreyi kullanmaya devam eder

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/abonelikler/{subscription.Id}/iptal",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["atPeriodEnd"] = "false" }))))
            .ShouldContain("hemen iptal edildi");

        var afterHard = await ApiOk<List<SubscriptionRow>>(HttpMethod.Get, "/v1/subscriptions", null,
            ("X-Api-Key", tenant.ApiKey));
        afterHard.Single(s => s.Id == subscription.Id).Status.ShouldBe("cancelled");
    }

    [Fact]
    public async Task Abonelik_karti_panelden_guncellenebilmeli()
    {
        var (tenant, email) = await SeedAsync();
        var plan = await ApiOk<PlanRow>(HttpMethod.Post, "/v1/plans",
            new { name = "Aylık", amountMinor = 15_000 }, ("X-Api-Key", tenant.ApiKey));

        async Task<CardRow> SaveCardAsync(string number) => await ApiOk<CardRow>(
            HttpMethod.Post, "/v1/vault/cards", new
            {
                cardNumber = number,
                expiryMonth = 12,
                expiryYear = DateTime.UtcNow.Year + 3,
                holderName = "ABONE MUSTERI",
            }, ("X-Api-Key", tenant.ApiKey));

        var first = await SaveCardAsync("4355084355084358");
        var second = await SaveCardAsync("5406675406675403");

        var subscription = await ApiOk<SubscriptionRow>(HttpMethod.Post, "/v1/subscriptions",
            new { planId = plan.Id, customerRef = "cust-kart", cardToken = first.Token },
            ("X-Api-Key", tenant.ApiKey));

        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);
        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/abonelikler/{subscription.Id}/kart",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["cardToken"] = second.Token }))))
            .ShouldContain("Kart güncellendi");

        var updated = (await ApiOk<List<SubscriptionRow>>(HttpMethod.Get, "/v1/subscriptions", null,
            ("X-Api-Key", tenant.ApiKey))).Single(s => s.Id == subscription.Id);
        updated.CardToken.ShouldBe(second.Token);
    }

    [Fact]
    public async Task Denetci_abonelik_iptal_edememeli()
    {
        var (tenant, _) = await SeedAsync();
        var plan = await ApiOk<PlanRow>(HttpMethod.Post, "/v1/plans",
            new { name = "Aylık", amountMinor = 12_000 }, ("X-Api-Key", tenant.ApiKey));
        var card = await ApiOk<CardRow>(HttpMethod.Post, "/v1/vault/cards", new
        {
            cardNumber = "4355084355084358",
            expiryMonth = 12,
            expiryYear = DateTime.UtcNow.Year + 3,
            holderName = "ABONE MUSTERI",
        }, ("X-Api-Key", tenant.ApiKey));
        var subscription = await ApiOk<SubscriptionRow>(HttpMethod.Post, "/v1/subscriptions",
            new { planId = plan.Id, customerRef = "cust-denetci", cardToken = card.Token },
            ("X-Api-Key", tenant.ApiKey));

        var auditorEmail = await AddUserAsync(tenant.ApiKey, "auditor");
        var panel = await LoginAsync(CreatePanelClient(), auditorEmail, tenant.Slug);

        var page = await panel.GetStringAsync("/abonelikler");
        page.ShouldContain("cust-denetci"); // okuma serbest
        page.ShouldNotContain("Hemen iptal");
        page.ShouldNotContain("Yeni plan");

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/abonelikler/{subscription.Id}/iptal",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["atPeriodEnd"] = "false" }))))
            .ShouldContain("'operations' rolü gerekli");

        (await ApiOk<List<SubscriptionRow>>(HttpMethod.Get, "/v1/subscriptions", null,
            ("X-Api-Key", tenant.ApiKey))).Single(s => s.Id == subscription.Id).Status.ShouldBe("active");
    }

    [Fact]
    public async Task Abonelik_ekrani_musteri_ve_duruma_gore_filtrelenebilmeli()
    {
        var (tenant, email) = await SeedAsync();
        var plan = await ApiOk<PlanRow>(HttpMethod.Post, "/v1/plans",
            new { name = "Aylık", amountMinor = 9_900 }, ("X-Api-Key", tenant.ApiKey));
        var card = await ApiOk<CardRow>(HttpMethod.Post, "/v1/vault/cards", new
        {
            cardNumber = "4355084355084358",
            expiryMonth = 12,
            expiryYear = DateTime.UtcNow.Year + 3,
            holderName = "ABONE MUSTERI",
        }, ("X-Api-Key", tenant.ApiKey));

        foreach (var customer in new[] { "alfa-tekstil", "beta-lojistik" })
            await ApiOk<SubscriptionRow>(HttpMethod.Post, "/v1/subscriptions",
                new { planId = plan.Id, customerRef = customer, cardToken = card.Token },
                ("X-Api-Key", tenant.ApiKey));

        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);

        var filtered = await panel.GetStringAsync("/abonelikler?musteri=alfa-tekstil");
        filtered.ShouldContain("alfa-tekstil");
        filtered.ShouldNotContain("beta-lojistik");

        var cancelledOnly = await panel.GetStringAsync("/abonelikler?durum=cancelled");
        cancelledOnly.ShouldContain("Abonelik yok");
    }

    // ---- İtiraz ekranı ---------------------------------------------------------

    private sealed record DisputeRow(
        string Id, string PaymentId, long AmountMinor, string Status, string Stage,
        double RemainingHours, bool Overdue, int EvidenceCount);

    private async Task<(TenantCreated Tenant, string Email, string DisputeId)> SeedDisputeAsync(
        DateTimeOffset? dueAt = null)
    {
        var (tenant, email) = await SeedAsync();

        var payment = await ApiOk<PaymentWithAction>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 50_000, currency = "TRY", confirm = true }, ("X-Api-Key", tenant.ApiKey));
        (await _api.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var dispute = await ApiOk<DisputeRow>(HttpMethod.Post, "/v1/disputes", new
        {
            paymentId = payment.Id,
            amountMinor = 50_000,
            reason = "poyra.dispute.product_not_received",
            evidenceDueAt = dueAt,
        }, ("X-Api-Key", tenant.ApiKey));

        return (tenant, email, dispute.Id);
    }

    [Fact]
    public async Task Itiraz_ekrani_yanan_dosyayi_one_cikarmali()
    {
        var (tenant, email, disputeId) = await SeedDisputeAsync(DateTimeOffset.UtcNow.AddHours(20));
        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);

        var page = await panel.GetStringAsync("/itirazlar");

        page.ShouldContain("Harcama itirazları");
        page.ShouldContain("Ürün teslim edilmedi"); // neden Türkçeleşiyor
        page.ShouldContain("500,00 ₺");
        page.ShouldContain("süresi 3 günden az"); // acil uyarısı
        page.ShouldContain("20 saat"); // kalan süre insan diliyle, "19,9 saat" değil
        page.ShouldContain(disputeId[..14]);
    }

    [Fact]
    public async Task Itiraz_detayinda_kanit_yuklenip_savunma_gonderilebilmeli()
    {
        var (tenant, email, disputeId) = await SeedDisputeAsync();
        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);

        // Belge yükle (multipart)
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 kargo tutanağı"));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "teslimat.pdf");
        form.Add(new StringContent("delivery_proof"), "kind");

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/itirazlar/{disputeId}/kanit", form)))
            .ShouldContain("'teslimat.pdf' eklendi");

        var detail = await panel.GetStringAsync($"/itirazlar/{disputeId}");
        detail.ShouldContain("teslimat.pdf");
        detail.ShouldContain("Teslim kanıtı");
        detail.ShouldContain("Bankaya ilet");

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/itirazlar/{disputeId}/gonder",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["summary"] = "Ürün 12.07.2026'da teslim edildi.",
                }))))
            .ShouldContain("incelemede");

        var after = await ApiOk<List<DisputeRow>>(HttpMethod.Get, "/v1/disputes", null,
            ("X-Api-Key", tenant.ApiKey));
        after.ShouldHaveSingleItem().Status.ShouldBe("under_review");
    }

    [Fact]
    public async Task Denetci_itiraz_savunamamali()
    {
        var (tenant, _, disputeId) = await SeedDisputeAsync();
        var auditorEmail = await AddUserAsync(tenant.ApiKey, "auditor");
        var panel = await LoginAsync(CreatePanelClient(), auditorEmail, tenant.Slug);

        var detail = await panel.GetStringAsync($"/itirazlar/{disputeId}");
        detail.ShouldContain("Ürün teslim edilmedi"); // okuma serbest
        detail.ShouldNotContain("Bankaya ilet");
        detail.ShouldNotContain("Savunmadan vazgeç");

        (await PanelRedirects.RevealAsync(panel, await panel.PostAsync($"/itirazlar/{disputeId}/vazgec", new FormUrlEncodedContent([]))))
            .ShouldContain("'operations' rolü gerekli");

        (await ApiOk<List<DisputeRow>>(HttpMethod.Get, "/v1/disputes", null, ("X-Api-Key", tenant.ApiKey)))
            .ShouldHaveSingleItem().Status.ShouldBe("open");
    }

    // ---- Gezinme -----------------------------------------------------------------

    [Fact]
    public async Task Yeni_ekranlar_menude_gorunmeli()
    {
        var (tenant, email) = await SeedAsync();
        var panel = await LoginAsync(CreatePanelClient(), email, tenant.Slug);

        // Menüde olmayan ekran, var olmayan ekrandır
        var page = await panel.GetStringAsync("/");
        page.ShouldContain("href=\"/webhooklar\"");
        page.ShouldContain("href=\"/ekip\"");
        page.ShouldContain("href=\"/abonelikler\"");
        page.ShouldContain("href=\"/itirazlar\"");
    }
}
