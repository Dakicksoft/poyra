using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Api;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Tenancy.Domain;
using Poyra.Panel;
using Poyra.SharedKernel.Tenancy;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F5.3 kimlik tamamlama: parola sıfırlama ve e-posta doğrulama. Postalar outbox'a yazılır
/// (SMTP kesintisi akışı bekletmesin); testler bağlantıyı outbox gövdesinden okur — gerçek
/// kullanıcının gelen kutusundan okuduğu metnin AYNISI.
/// </summary>
[Collection("postgres")]
public sealed class PasswordAndVerificationTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string OldPassword = "eski-parola-123";
    private const string NewPassword = "yeni-parola-456";

    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory;
    private readonly WebApplicationFactory<PanelEntryPoint> _panelFactory;
    private readonly HttpClient _api;

    public PasswordAndVerificationTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        void Configure(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Panel:Antiforgery:Enforce", "false"); // form token kurulumu ayrı testte
            builder.UseSetting("Platform:AdminKey", AdminKey);
            builder.UseSetting("Poyra:PublicBaseUrl", "http://localhost");
            builder.UseSetting("Poyra:PanelBaseUrl", "http://localhost");
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

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey, Guid? OwnerUserId);
    private sealed record AckDto(bool Accepted, string Message);
    private sealed record LoginDto(string AccessToken, string RefreshToken);
    private sealed record MeDto(Guid UserId, string Email, string DisplayName, Guid TenantId, string Role, bool EmailVerified);

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

    private async Task<(TenantCreated Tenant, string Email)> SeedTenantAsync()
    {
        var email = $"kimlik-{Guid.NewGuid():N}@ornek.com";
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "Kimlik Testi A.Ş.",
            slug = "kimlik-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = OldPassword,
            ownerName = "Kimlik Kullanıcısı",
        }, ("X-Platform-Key", AdminKey));
        return (tenant, email);
    }

    /// <summary>Kullanıcının gelen kutusu: outbox satırının metin gövdesi.</summary>
    private async Task<List<EmailMessageRecord>> InboxAsync(string email, string? purpose = null)
    {
        using var scope = _apiFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();

        return await db.EmailMessages.AsNoTracking()
            .Where(m => m.ToEmail == email && (purpose == null || m.Purpose == purpose))
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();
    }

    /// <summary>Hedef posta gönderilene kadar teslimat işini turlar.</summary>
    private async Task<EmailMessageRecord> DrainUntilSentAsync(string email, string purpose)
    {
        for (var round = 0; round < 20; round++)
        {
            using (var scope = _apiFactory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();
                await scope.ServiceProvider
                    .GetRequiredService<Poyra.Modules.Tenancy.Infrastructure.EmailDispatchJob>()
                    .DispatchPendingAsync();
            }

            var mail = (await InboxAsync(email, purpose)).Single();
            if (mail.Status != EmailStatus.Pending)
                return mail;
        }

        throw new InvalidOperationException($"{purpose} postası 20 turda gönderilemedi.");
    }

    private static string TokenFromBody(EmailMessageRecord record, string queryKey = "belirteç")
    {
        var match = Regex.Match(record.BodyText, queryKey + @"=([A-Za-z0-9_\-%]+)");
        match.Success.ShouldBeTrue($"posta gövdesinde {queryKey} yok:\n{record.BodyText}");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    // ---- Senaryolar ----------------------------------------------------------

    [Fact]
    public async Task Isyeri_acilisinda_dogrulama_postasi_kuyruga_girmeli()
    {
        var (_, email) = await SeedTenantAsync();

        var inbox = await InboxAsync(email, "email_verification");
        var mail = inbox.ShouldHaveSingleItem();
        mail.Subject.ShouldBe("Poyra e-posta doğrulama");
        mail.Status.ShouldBe(EmailStatus.Pending); // teslimat ayrı iştir
        mail.BodyText.ShouldContain("/eposta-dogrula?belirteç=");
        mail.BodyText.ShouldContain("3 gün geçerlidir");
    }

    [Fact]
    public async Task Dogrulama_baglantisi_adresi_dogrulamali_ve_tek_kullanimlik_olmali()
    {
        var (tenant, email) = await SeedTenantAsync();
        var token = TokenFromBody((await InboxAsync(email, "email_verification")).Single());

        var login = await SendOk<LoginDto>(HttpMethod.Post, "/v1/auth/login",
            new { email, password = OldPassword, tenantSlug = tenant.Slug });
        var auth = ("Authorization", $"Bearer {login.AccessToken}");

        (await SendOk<MeDto>(HttpMethod.Get, "/v1/auth/me", null, auth)).EmailVerified.ShouldBeFalse();

        var verified = await SendOk<AckDto>(HttpMethod.Post, "/v1/auth/email/verify", new { token });
        verified.Accepted.ShouldBeTrue();

        (await SendOk<MeDto>(HttpMethod.Get, "/v1/auth/me", null, auth)).EmailVerified.ShouldBeTrue();

        // İkinci tıklama: belirteç tüketildi
        var replay = await Send(HttpMethod.Post, "/v1/auth/email/verify", new { token });
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await replay.Content.ReadAsStringAsync()).ShouldContain("verify_token_invalid");
    }

    [Fact]
    public async Task Parola_sifirlama_acik_oturumlari_kapatmali()
    {
        var (tenant, email) = await SeedTenantAsync();

        // Kullanıcı iki cihazdan girmiş — ikisinin de refresh token'ı var
        var first = await SendOk<LoginDto>(HttpMethod.Post, "/v1/auth/login",
            new { email, password = OldPassword, tenantSlug = tenant.Slug });
        var second = await SendOk<LoginDto>(HttpMethod.Post, "/v1/auth/login",
            new { email, password = OldPassword, tenantSlug = tenant.Slug });

        await SendOk<AckDto>(HttpMethod.Post, "/v1/auth/password/forgot", new { email });
        var token = TokenFromBody((await InboxAsync(email, "password_reset")).Single());

        var reset = await SendOk<AckDto>(HttpMethod.Post, "/v1/auth/password/reset",
            new { token, newPassword = NewPassword });
        reset.Accepted.ShouldBeTrue();
        reset.Message.ShouldContain("2 oturum kapatıldı");

        // Sıfırlamanın ASIL değeri: ele geçirilmiş oturumlar da düşer
        foreach (var session in new[] { first, second })
        {
            var refresh = await Send(HttpMethod.Post, "/v1/auth/refresh",
                new { refreshToken = session.RefreshToken });
            refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Eski parola ölü, yeni parola çalışıyor
        (await Send(HttpMethod.Post, "/v1/auth/login",
            new { email, password = OldPassword, tenantSlug = tenant.Slug }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, "/v1/auth/login",
            new { email, password = NewPassword, tenantSlug = tenant.Slug }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Kullanıcı değişimden haberdar edilir
        (await InboxAsync(email, "password_changed")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Sifirlama_belirteci_tek_kullanimlik_olmali_ve_yenisi_eskisini_oldurmeli()
    {
        var (_, email) = await SeedTenantAsync();

        await SendOk<AckDto>(HttpMethod.Post, "/v1/auth/password/forgot", new { email });
        var firstToken = TokenFromBody((await InboxAsync(email, "password_reset"))[0]);

        // Kullanıcı "tekrar gönder"e bastı: eski bağlantı ÖLMELİ (tek canlı bağlantı)
        await SendOk<AckDto>(HttpMethod.Post, "/v1/auth/password/forgot", new { email });
        var secondToken = TokenFromBody((await InboxAsync(email, "password_reset"))[1]);
        secondToken.ShouldNotBe(firstToken);

        var stale = await Send(HttpMethod.Post, "/v1/auth/password/reset",
            new { token = firstToken, newPassword = NewPassword });
        stale.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await SendOk<AckDto>(HttpMethod.Post, "/v1/auth/password/reset",
            new { token = secondToken, newPassword = NewPassword });

        // Aynı belirteç ikinci kez kullanılamaz
        var replay = await Send(HttpMethod.Post, "/v1/auth/password/reset",
            new { token = secondToken, newPassword = "baska-parola-789" });
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Kayitli_olmayan_adres_ayni_yaniti_vermeli_ve_posta_uretmemeli()
    {
        var bilinmeyen = $"yok-{Guid.NewGuid():N}@ornek.com";
        var (_, kayitli) = await SeedTenantAsync();

        var unknown = await SendOk<AckDto>(HttpMethod.Post, "/v1/auth/password/forgot",
            new { email = bilinmeyen });
        var known = await SendOk<AckDto>(HttpMethod.Post, "/v1/auth/password/forgot",
            new { email = kayitli });

        // Hesap keşfine kapalı: yanıt AYNI
        unknown.Accepted.ShouldBe(known.Accepted);
        unknown.Message.ShouldBe(known.Message);

        // Ama posta yalnız gerçek adrese üretilir
        (await InboxAsync(bilinmeyen)).ShouldBeEmpty();
        (await InboxAsync(kayitli, "password_reset")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Zayif_parola_reddedilmeli()
    {
        var (_, email) = await SeedTenantAsync();
        await SendOk<AckDto>(HttpMethod.Post, "/v1/auth/password/forgot", new { email });
        var token = TokenFromBody((await InboxAsync(email, "password_reset")).Single());

        var weak = await Send(HttpMethod.Post, "/v1/auth/password/reset",
            new { token, newPassword = "kisa" });
        weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Belirteç harcanmadı — kullanıcı düzgün parolayla tekrar deneyebilir
        (await Send(HttpMethod.Post, "/v1/auth/password/reset",
            new { token, newPassword = NewPassword })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Gonderilen_postanin_govdesi_silinmeli()
    {
        var (_, email) = await SeedTenantAsync();
        await SendOk<AckDto>(HttpMethod.Post, "/v1/auth/password/forgot", new { email });

        // İş her turda EN ESKİ 50 postayı alır ve email_messages RLS'siz platform
        // tablosudur: tüm takım koştuğunda önümüzde başka testlerin postaları birikir.
        // Tek tur çalıştırmak bizimkine sıra gelmesini VARSAYMAK olurdu.
        var mail = await DrainUntilSentAsync(email, "password_reset");
        mail.Status.ShouldBe(EmailStatus.Sent);
        mail.SentAt.ShouldNotBeNull();
        // Gövdede tek kullanımlık belirteç vardı; gönderildikten sonra kalıcı bir sır olmamalı
        mail.BodyText.ShouldNotContain("prst_");
        mail.BodyHtml.ShouldBeEmpty();
    }

    [Fact]
    public async Task Panelden_sifirlama_akisi_calismali()
    {
        var (tenant, email) = await SeedTenantAsync();
        var panel = _panelFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        (await panel.GetStringAsync("/giris")).ShouldContain("Parolamı unuttum");

        var ask = await panel.PostAsync("/parola-unuttum",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email }));
        ask.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        ask.Headers.Location!.ToString().ShouldContain("/giris?sonuc=");

        var token = TokenFromBody((await InboxAsync(email, "password_reset")).Single());

        // Belirteç sayfada gizli alanda taşınır (adres satırında kalmasın)
        var form = await panel.GetStringAsync($"/parola-sifirla?belirteç={Uri.EscapeDataString(token)}");
        form.ShouldContain("Yeni parola");
        form.ShouldContain(token);

        var mismatch = await panel.PostAsync("/parola-sifirla",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["belirteç"] = token, ["password"] = NewPassword, ["passwordRepeat"] = "farkli-parola-123",
            }));
        mismatch.Headers.Location!.ToString().ShouldContain("hata=");

        var done = await panel.PostAsync("/parola-sifirla",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["belirteç"] = token, ["password"] = NewPassword, ["passwordRepeat"] = NewPassword,
            }));
        done.Headers.Location!.ToString().ShouldContain("/giris?sonuc=");

        // Yeni parolayla panele girilebiliyor
        var login = await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = NewPassword, ["tenantSlug"] = tenant.Slug,
        }));
        login.Headers.Location!.ToString().ShouldBe("/");
    }

    [Fact]
    public async Task Panel_dogrulanmamis_adres_icin_uyari_gostermeli()
    {
        var (tenant, email) = await SeedTenantAsync();
        var panel = _panelFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await panel.PostAsync("/giris", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = OldPassword, ["tenantSlug"] = tenant.Slug,
        }));

        var before = await panel.GetStringAsync("/");
        before.ShouldContain("henüz doğrulanmadı");

        // Doğrulama bağlantısı GET'te belirteci TÜKETMEZ (SafeLinks ön-açması yakmasın) —
        // sayfa onay butonu gösterir, doğrulama POST ile yapılır
        var token = TokenFromBody((await InboxAsync(email, "email_verification")).Single());
        var page = await panel.GetStringAsync($"/eposta-dogrula?belirteç={Uri.EscapeDataString(token)}");
        page.ShouldContain("doğrula");

        var confirmed = await panel.PostAsync("/eposta-dogrula", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["belirteç"] = token }));
        confirmed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var verify = await panel.GetStringAsync(confirmed.Headers.Location!.ToString());
        verify.ShouldContain("doğruland");

        // Uyarı, çıkış/giriş gerekmeden kaybolmalı — durum defterden okunuyor
        (await panel.GetStringAsync("/")).ShouldNotContain("henüz doğrulanmadı");
    }
}
