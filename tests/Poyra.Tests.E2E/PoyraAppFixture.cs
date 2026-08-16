using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Playwright;
using Npgsql;
using Poyra.Api;
using Poyra.Api.Database;
using Poyra.Checkout;
using Poyra.Panel;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace Poyra.Tests.E2E;

/// <summary>
/// E2E zemini: gerçek Postgres 18 + gerçek portlarda Api/Panel/Checkout + gerçek tarayıcı.
/// Tümü test sürecinde ayağa kalkar — dışarıda ayakta duran servise bağımlılık yok,
/// tek komut (dotnet test) her şeyi kurar.
///
/// Entegrasyon fikstürüyle aynı rol modeli: migration'lar sahip 'poyra' rolüyle koşar,
/// uygulamalar 'poyra_app' (NOBYPASSRLS) ile bağlanır — RLS burada da gerçekten sınanır.
/// </summary>
public sealed class PoyraAppFixture : IAsyncLifetime
{
    public const string AdminKey = "e2e-admin-key";
    public const string Parola = "e2e-parola-123";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithUsername("poyra")
        .WithPassword("poyra_pw")
        .WithDatabase("poyra")
        .Build();

    private KestrelFactory<ApiEntryPoint>? _api;
    private KestrelFactory<PanelEntryPoint>? _panel;
    private KestrelFactory<CheckoutEntryPoint>? _checkout;
    private IPlaywright? _playwright;

    public string ApiAdres { get; private set; } = "";
    public string PanelAdres { get; private set; } = "";
    public string CheckoutAdres { get; private set; } = "";

    /// <summary>Veri hazırlığı için API istemcisi — senaryo kurulumu tarayıcıdan yapılmaz.</summary>
    public HttpClient Api { get; private set; } = null!;

    public IBrowser Tarayici { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var sahipCs = _postgres.GetConnectionString();
        var uygulamaCs = new NpgsqlConnectionStringBuilder(sahipCs)
        {
            Username = "poyra_app",
            Password = "poyra_app_pw",
        }.ConnectionString;

        await using (var baglanti = new NpgsqlConnection(sahipCs))
        {
            await baglanti.OpenAsync();
            // Üretimdeki initdb betiğinin kendisi (csproj kopyalar) — migration'lardan ÖNCE
            await using var komut = new NpgsqlCommand(
                await File.ReadAllTextAsync("01-app-role.sql"), baglanti);
            await komut.ExecuteNonQueryAsync();
        }

        await DatabaseMigrator.RunAsync(sahipCs);

        var (apiPort, panelPort, checkoutPort) = (BosPortAyir(), BosPortAyir(), BosPortAyir());
        ApiAdres = $"http://127.0.0.1:{apiPort}";
        PanelAdres = $"http://127.0.0.1:{panelPort}";
        CheckoutAdres = $"http://127.0.0.1:{checkoutPort}";

        void Ortak(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); // Development değil → dev kapıları kapalı

            // Statik varlık manifestini ELLE yükle. Varsayılan host bunu yalnız
            // Development'ta yapar; "Testing"te yapmaz ve sonuç sinsi: MapStaticAssets
            // rotayı tanır (200 döner) ama tarayıcı gzip istediği için sunucu
            // poyra.css.gz'yi arar — o dosya kaynak wwwroot'ta değil, derleme
            // çıktısındadır. Tarayıcı BOŞ bir stil dosyası alır, sayfa stilsiz açılır
            // ve _framework/blazor.web.js 500'e düşer (interaktif adalar çalışmaz).
            builder.UseStaticWebAssets();
            builder.UseSetting("ConnectionStrings:Poyra", uygulamaCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", sahipCs);
            builder.UseSetting("Database:AutoMigrate", "false"); // fikstür zaten migrate etti
            builder.UseSetting("Platform:AdminKey", AdminKey);
            builder.UseSetting("Poyra:PublicBaseUrl", ApiAdres);       // banka callback'i buraya döner
            builder.UseSetting("Poyra:CheckoutBaseUrl", CheckoutAdres); // ödeme bağlantıları buraya
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Poyra:JwtKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            builder.UseSetting("Poyra:VaultKey", Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
            // Antiforgery BİLEREK açık bırakıldı: gerçek tarayıcı gerçek belirteçle post etsin.
        }

        _api = new KestrelFactory<ApiEntryPoint>(apiPort, Ortak);
        _panel = new KestrelFactory<PanelEntryPoint>(panelPort, Ortak);
        _checkout = new KestrelFactory<CheckoutEntryPoint>(checkoutPort, Ortak);
        _api.Baslat();
        _panel.Baslat();
        _checkout.Baslat();

        Api = new HttpClient { BaseAddress = new Uri(ApiAdres) };

        // Uygulamaları ISIT. İlk istek pahalıdır: 16 EF modeli kurulur, Razor bileşenleri
        // derlenir, Hangfire deposu hazırlanır. İlk koşuda tam olarak buna takıldık —
        // Playwright'ın 30 sn'lik varsayılan gezinme zaman aşımı soğuk uygulamaya yetmedi
        // ve testler "sayfa açılmıyor" diye düştü. Isınma testin işi değil, zeminin işi.
        await Task.WhenAll(
            IsitAsync(ApiAdres, "/health/live"),
            IsitAsync(PanelAdres, "/giris"),
            IsitAsync(CheckoutAdres, "/l/isinma"));

        // Tarayıcı ikilisi yoksa indirir; kuruluysa saniyenin altında döner.
        var kod = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (kod != 0)
            throw new InvalidOperationException($"Playwright chromium kurulumu başarısız (çıkış {kod}).");

        _playwright = await Playwright.CreateAsync();
        Tarayici = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (Tarayici is not null) await Tarayici.CloseAsync();
        _playwright?.Dispose();
        Api?.Dispose();
        _checkout?.Dispose();
        _panel?.Dispose();
        _api?.Dispose();
        await _postgres.DisposeAsync();
    }

    // ---- Senaryo kurulumu (API üzerinden — hızlı ve tarayıcıdan bağımsız) --------

    public sealed record Isyeri(Guid TenantId, string Slug, string ApiKey);
    private sealed record HesapDto(Guid Id);
    public sealed record BaglantiDto(
        string Id, string Slug, string Url, long? AmountMinor, string Currency, string Description);
    public sealed record OdemeDto(string Id, string Status);

    /// <summary>İşyeri + sahip kullanıcı + mockbank POS hesabı — çoğu senaryonun başlangıcı.</summary>
    public async Task<(Isyeri Isyeri, string Eposta)> IsyeriKurAsync(string etiket)
    {
        var eposta = $"e2e-{Guid.NewGuid():N}@ornek.com";
        var isyeri = await ApiOk<Isyeri>(HttpMethod.Post, "/v1/tenants", new
        {
            name = etiket,
            slug = "e2e-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = eposta,
            ownerPassword = Parola,
            ownerName = "E2E Kullanıcısı",
        }, ("X-Platform-Key", AdminKey));

        await ApiOk<HesapDto>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", isyeri.ApiKey));

        return (isyeri, eposta);
    }

    public Task<BaglantiDto> OdemeBaglantisiOlusturAsync(string apiKey, object govde)
        => ApiOk<BaglantiDto>(HttpMethod.Post, "/v1/payment-links", govde, ("X-Api-Key", apiKey));

    public Task<OdemeDto> OdemeOkuAsync(string apiKey, string odemeId)
        => ApiOk<OdemeDto>(HttpMethod.Get, $"/v1/payments/{odemeId}", null, ("X-Api-Key", apiKey));

    private sealed record SonrakiAdim(string Url, Dictionary<string, string> Fields);
    private sealed record OdemeAyrinti(string Id, string Status, SonrakiAdim? NextAction);

    /// <summary>
    /// API üzerinden baştan sona başarılı bir ödeme yapar (oluştur → rota → "banka"
    /// formunu callback'e gönder). Tarayıcıyı meşgul etmeden veri üretmek için:
    /// canlı akış gibi senaryolarda ödeme DIŞARIDAN gelmeli ki bildirim yolu sınansın.
    /// Kuruş %100 == 99/98 mockbank'ta ret demektir — çağıran bunu bilerek tutar seçmeli.
    /// </summary>
    public async Task<string> OdemeYapAsync(string apiKey, long kurus)
    {
        var odeme = await ApiOk<OdemeAyrinti>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = kurus, currency = "TRY", confirm = true, description = "E2E" },
            ("X-Api-Key", apiKey));

        odeme.NextAction.ShouldNotBeNull("mockbank hesabı tanımlı değil mi?");
        var yanit = await Api.PostAsync(odeme.NextAction.Url,
            new FormUrlEncodedContent(odeme.NextAction.Fields));
        yanit.StatusCode.ShouldBe(HttpStatusCode.OK, await yanit.Content.ReadAsStringAsync());

        return odeme.Id;
    }

    public async Task<T> ApiOk<T>(
        HttpMethod yontem, string yol, object? govde, params (string Ad, string Deger)[] basliklar)
    {
        var istek = new HttpRequestMessage(yontem, yol);
        if (govde is not null)
            istek.Content = JsonContent.Create(govde);
        foreach (var (ad, deger) in basliklar)
            istek.Headers.Add(ad, deger);

        var yanit = await Api.SendAsync(istek);
        yanit.StatusCode.ShouldBe(HttpStatusCode.OK, await yanit.Content.ReadAsStringAsync());
        return (await yanit.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>
    /// Uygulamanın ilk isteğini gönderir ve boru hattının kurulmasını bekler. Durum kodu
    /// önemli değil (ısınma yolu 404 de dönebilir); önemli olan yanıtın gelmesi.
    /// </summary>
    private static async Task IsitAsync(string adres, string yol)
    {
        using var istemci = new HttpClient
        {
            BaseAddress = new Uri(adres),
            Timeout = TimeSpan.FromMinutes(2),
        };
        using var yanit = await istemci.GetAsync(yol);
        _ = await yanit.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// İşletim sisteminden boş bir port alır ve hemen bırakır. Bırakma ile Kestrel'in
    /// bağlanması arasında teorik bir yarış var; pratikte tek makinede sorun çıkarmıyor
    /// ve karşılığında üç uygulamanın birbirinin adresini önceden bilmesini sağlıyor.
    /// </summary>
    private static int BosPortAyir()
    {
        using var dinleyici = new TcpListener(IPAddress.Loopback, 0);
        dinleyici.Start();
        var port = ((IPEndPoint)dinleyici.LocalEndpoint).Port;
        dinleyici.Stop();
        return port;
    }
}

[CollectionDefinition("e2e")]
public sealed class E2ECollection : ICollectionFixture<PoyraAppFixture>;
