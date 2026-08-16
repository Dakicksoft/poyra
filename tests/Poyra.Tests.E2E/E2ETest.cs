using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace Poyra.Tests.E2E;

/// <summary>
/// E2E testlerinin ortak zemini: her test için temiz tarayıcı bağlamı (çerez taşımaz),
/// tr-TR yerel ayarı ve İstanbul saat dilimi — para/tarih biçimi üretimdeki gibi görünsün.
///
/// Her testin izi (trace) kaydedilir. Başarısız testin izini ayırt etmek için xUnit'in
/// "yalnız hatalı testin çıktısını göster" davranışından yararlanıyoruz: dosya yolu test
/// çıktısına yazılır, dolayısıyla kırmızı testin yanında hangi zip'i açacağınız yazar.
/// İz, Playwright'ın kendi görüntüleyicisiyle açılır:
///     dotnet tool run playwright show-trace &lt;zip&gt;
/// </summary>
[Collection("e2e")]
public abstract class E2ETest(PoyraAppFixture uygulama, ITestOutputHelper cikti) : IAsyncLifetime
{
    private static int _sayac;

    protected PoyraAppFixture Uygulama { get; } = uygulama;
    protected IBrowserContext Baglam { get; private set; } = null!;
    protected IPage Sayfa { get; private set; } = null!;

    private string _kanitKlasoru = "";

    public async Task InitializeAsync()
    {
        _kanitKlasoru = Path.Combine(
            KanitKok(), $"{GetType().Name}-{Interlocked.Increment(ref _sayac):D2}");
        Directory.CreateDirectory(_kanitKlasoru);

        Baglam = await Uygulama.Tarayici.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = Uygulama.PanelAdres,
            Locale = "tr-TR",
            TimezoneId = "Europe/Istanbul",
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        await Baglam.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true,
        });

        // Dış dünyaya çıkışı kes. Sayfalar Google Fonts'tan stil çekiyor ve tarayıcının
        // "load" olayı o isteği BEKLİYOR: ilk koşuda testler tam olarak buna asıldı
        // (istek -1 ile takılı kaldı, gezinme 30 sn'de zaman aşımına düştü). E2E testi
        // üçüncü tarafın erişilebilirliğine bağlı olamaz — kendi hostlarımız dışındaki
        // her istek düşürülür. (Fontun kendisi ayrı bir konu: bkz. docs/test-piramidi.md)
        await Baglam.RouteAsync("**/*", async rota =>
        {
            var adres = rota.Request.Url;
            var bizim = adres.StartsWith(Uygulama.PanelAdres, StringComparison.Ordinal)
                || adres.StartsWith(Uygulama.ApiAdres, StringComparison.Ordinal)
                || adres.StartsWith(Uygulama.CheckoutAdres, StringComparison.Ordinal);

            if (bizim)
            {
                await rota.ContinueAsync();
                return;
            }

            // Düşürmek yerine BOŞ 200 ile karşılıyoruz. Abort, konsola "Failed to load
            // resource" hatası basıyor ve testlerin konsol hatası doğrulamasını kendi
            // gürültümüzle bozuyordu — gerçek bir JS hatasını fark edemez hâle gelirdik.
            await rota.FulfillAsync(new RouteFulfillOptions { Status = 200, Body = "" });
        });

        Sayfa = await Baglam.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        var iz = Path.Combine(_kanitKlasoru, "iz.zip");
        await Baglam.Tracing.StopAsync(new TracingStopOptions { Path = iz });

        try
        {
            await Sayfa.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(_kanitKlasoru, "son-ekran.png"),
                FullPage = true,
            });
        }
        catch (PlaywrightException)
        {
            // Sayfa kapandıysa/çöktüyse ekran görüntüsü alınamaz — iz zaten kaydedildi.
        }

        cikti.WriteLine($"E2E kanıtları: {_kanitKlasoru}");
        await Baglam.CloseAsync();
    }

    /// <summary>
    /// Panele giriş yapar.
    ///
    /// Alanlar KULLANICININ gördüğü etiketle seçilir — CSS sınıfı ya da name değil.
    /// Böylece test, ekran okuyucunun okuduğu adla aynı şeye bakar: etiket bağı bozulursa
    /// test kırılır. (Parolada Exact şart: aksi hâlde "Parola" alt dizesi, göster/gizle
    /// düğmesinin "Parolayı göster veya gizle" aria-label'ıyla da eşleşir.)
    /// </summary>
    protected async Task GirisYapAsync(string eposta, string isyeriKodu)
    {
        await Sayfa.GotoAsync($"{Uygulama.PanelAdres}/giris");
        await Sayfa.GetByLabel("E-posta").FillAsync(eposta);
        await Sayfa.GetByLabel("Parola", new() { Exact = true }).FillAsync(PoyraAppFixture.Parola);
        await Sayfa.GetByLabel("İşyeri kodu").FillAsync(isyeriKodu);
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Giriş yap" }).ClickAsync();
    }

    /// <summary>Depo kökündeki artifacts/e2e (bin altına dağılmasın diye yukarı çıkılır).</summary>
    private static string KanitKok()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "Poyra.slnx")))
            dizin = dizin.Parent;

        return Path.Combine(dizin?.FullName ?? AppContext.BaseDirectory, "artifacts", "e2e");
    }
}
