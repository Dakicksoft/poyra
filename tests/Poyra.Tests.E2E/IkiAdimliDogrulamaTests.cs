using Microsoft.Playwright;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Poyra.Tests.E2E;

/// <summary>
/// TOTP 2FA yolculuğu. Kapsam ölçümü bu özelliğin DAVRANIŞININ hiç test edilmediğini
/// gösterdi: TotpTests yalnız RFC 6238 aritmetiğini doğruluyor, dokuz handler
/// (BeginTotpEnrollment … VerifyTotp) ve /guvenlik, /giris/dogrulama sayfaları %0'dı.
/// Bir güvenlik özelliğinin kriptosunun doğru olması, akışının doğru olduğunu göstermez.
/// </summary>
public sealed class IkiAdimliDogrulamaTests(PoyraAppFixture uygulama, ITestOutputHelper cikti)
    : E2ETest(uygulama, cikti)
{
    [Fact]
    public async Task Kurulum_sonrasi_giris_ikinci_adim_ister_ve_cihaz_hatirlanir()
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E 2FA");
        await GirisYapAsync(eposta, isyeri.Slug);

        // --- Kurulum ---
        await Sayfa.GotoAsync($"{Uygulama.PanelAdres}/guvenlik");
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "2FA kurulumunu başlat" }).ClickAsync();

        // QR gerçekten üretiliyor mu (tarayıcı /guvenlik/2fa/qr.png'i indirir)
        var qr = Sayfa.GetByRole(AriaRole.Img, new() { Name = "Authenticator kurulum QR kodu" });
        await Assertions.Expect(qr).ToBeVisibleAsync();
        // decode() indirmeyi BEKLER ve görsel bozuksa reddeder. Sadece naturalWidth bakmak
        // yetmez: <img> genişlik/yükseklik özniteliğiyle "görünür" sayılır, bitmap gelmese de.
        await qr.EvaluateAsync("img => img.decode()");
        (await qr.EvaluateAsync<int>("img => img.naturalWidth")).ShouldBeGreaterThan(0);

        var anahtar = await Sayfa.Locator(".secret").InnerTextAsync();
        await Sayfa.GetByLabel("Uygulamadaki 6 haneli kod")
            .FillAsync(TotpKodu.Uret(anahtar, DateTimeOffset.UtcNow));
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Doğrula ve 2FA'yı aç" }).ClickAsync();

        await Assertions.Expect(Sayfa.GetByText("İki adımlı doğrulama açıldı")).ToBeVisibleAsync();

        // --- Çıkış → giriş: artık ikinci adım istenmeli ---
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Çıkış" }).ClickAsync();
        await GirisYapAsync(eposta, isyeri.Slug);

        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = "İki adımlı doğrulama" }))
            .ToBeVisibleAsync();
        Sayfa.Url.ShouldContain("/giris/dogrulama");

        // Yanlış kod içeri almamalı
        await Sayfa.GetByLabel("Doğrulama kodu").FillAsync("000000");
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Doğrula ve giriş yap" }).ClickAsync();
        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
        Sayfa.Url.ShouldContain("/giris/dogrulama");

        // Doğru kod + "bu cihazı hatırla"
        await Sayfa.GetByLabel("Doğrulama kodu")
            .FillAsync(TotpKodu.Uret(anahtar, DateTimeOffset.UtcNow));
        await Sayfa.GetByLabel("Bu cihazı 30 gün hatırla").CheckAsync();
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Doğrula ve giriş yap" }).ClickAsync();

        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = "Genel Bakış" }))
            .ToBeVisibleAsync();

        // --- Hatırlanan cihaz: bir sonraki girişte ikinci adım atlanmalı ---
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Çıkış" }).ClickAsync();
        await GirisYapAsync(eposta, isyeri.Slug);

        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = "Genel Bakış" }))
            .ToBeVisibleAsync();
        Sayfa.Url.ShouldNotContain("/giris/dogrulama");
    }

    [Fact]
    public async Task Kapatma_onay_kutusu_isaretlenmeden_tarayici_formu_gondermemeli()
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E 2FA Kapat");
        await GirisYapAsync(eposta, isyeri.Slug);

        await Sayfa.GotoAsync($"{Uygulama.PanelAdres}/guvenlik");
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "2FA kurulumunu başlat" }).ClickAsync();

        var anahtar = await Sayfa.Locator(".secret").InnerTextAsync();
        await Sayfa.GetByLabel("Uygulamadaki 6 haneli kod")
            .FillAsync(TotpKodu.Uret(anahtar, DateTimeOffset.UtcNow));
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Doğrula ve 2FA'yı aç" }).ClickAsync();
        await Assertions.Expect(Sayfa.GetByText("İki adımlı doğrulama açıldı")).ToBeVisibleAsync();

        // Tehlikeli aksiyon deseni: uç nokta değişmeden TARAYICI zorlar. Kod doğru olsa
        // bile onay kutusu işaretsizken form gönderilemez. Bunu yalnız bu katman görebilir —
        // HTTP seviyesindeki test formu doldurmadan doğrudan POST eder ve kuralı atlar.
        var kapatmaFormu = Sayfa.Locator("form[action='/guvenlik/2fa/kapat']");
        await kapatmaFormu.GetByLabel("Doğrulama kodu")
            .FillAsync(TotpKodu.Uret(anahtar, DateTimeOffset.UtcNow));

        (await kapatmaFormu.EvaluateAsync<bool>("form => form.checkValidity()")).ShouldBeFalse();

        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "2FA'yı kapat" }).ClickAsync();
        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = "Kapat" }))
            .ToBeVisibleAsync(); // hâlâ aynı sayfa: gönderim engellendi

        // Onay verilince kapanmalı
        await kapatmaFormu.GetByLabel("Hesabım yalnız parolayla korunacak").CheckAsync();
        await kapatmaFormu.GetByLabel("Doğrulama kodu")
            .FillAsync(TotpKodu.Uret(anahtar, DateTimeOffset.UtcNow));
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "2FA'yı kapat" }).ClickAsync();

        await Assertions.Expect(Sayfa.GetByText("İki adımlı doğrulama kapatıldı")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Isyeri_politikasi_owner_ve_admin_icin_2fa_zorunlu_kilabilir()
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E Politika");
        await GirisYapAsync(eposta, isyeri.Slug);

        await Sayfa.GotoAsync($"{Uygulama.PanelAdres}/guvenlik");
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Owner/admin için zorunlu kıl" }).ClickAsync();
        await Assertions.Expect(Sayfa.GetByText("2FA zorunlu kılındı")).ToBeVisibleAsync();

        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Zorunluluğu kaldır" }).ClickAsync();
        await Assertions.Expect(Sayfa.GetByText("Owner/admin için zorunlu kıl")).ToBeVisibleAsync();
    }
}

/// <summary>
/// Test üretecinin kendisi doğru mu? RFC 6238 Ek B vektörleriyle sabitlenir —
/// üreteç bozulursa 2FA testleri "ürün bozuk" diye kırmızıya döner, sebebi burada görünür.
/// </summary>
public sealed class TotpKoduTests
{
    private const string RfcAnahtar = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Rfc6238_vektorlerini_uretmeli(long unixSaniye, string kod)
        => TotpKodu.Uret(RfcAnahtar, DateTimeOffset.FromUnixTimeSeconds(unixSaniye)).ShouldBe(kod);

    [Fact]
    public void Panelin_4erli_gosterdigi_anahtari_kabul_etmeli()
        => TotpKodu.Uret("GEZD GNBV GY3T QOJQ GEZD GNBV GY3T QOJQ",
            DateTimeOffset.FromUnixTimeSeconds(59)).ShouldBe("287082");
}
