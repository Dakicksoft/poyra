using Microsoft.Playwright;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Poyra.Tests.E2E;

/// <summary>
/// Panelin yalnız tarayıcıda görülebilen davranışları: tek kullanımlık sır, CSS ile
/// çalışan mobil çekmece ve kapsam ölçümünde %0 çıkan ekranların gerçekten açılması.
/// </summary>
public sealed class PanelDavranisTests(PoyraAppFixture uygulama, ITestOutputHelper cikti)
    : E2ETest(uygulama, cikti)
{
    [Fact]
    public async Task Webhook_sirri_yalnizca_bir_kez_gosterilir()
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E Webhook");
        await GirisYapAsync(eposta, isyeri.Slug);

        await Sayfa.GotoAsync($"{Uygulama.PanelAdres}/webhooklar");
        await Sayfa.GetByLabel("Adres (HTTPS)").FillAsync("https://sistemim.ornek.com/poyra");
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Ekle" }).ClickAsync();

        var sir = Sayfa.Locator(".secret");
        await Assertions.Expect(sir).ToBeVisibleAsync();
        var deger = (await sir.InnerTextAsync()).Trim();
        deger.ShouldNotBeNullOrWhiteSpace();

        // Sır URL'de TAŞINMAZ — adres çubuğuna düşseydi tarayıcı geçmişine ve
        // sunucu erişim günlüklerine yazılırdı.
        Sayfa.Url.ShouldNotContain(deger);

        // Yenilemede bir daha gösterilmemeli (OneTimeSecretStash tek kullanımlıktır)
        await Sayfa.ReloadAsync();
        await Assertions.Expect(Sayfa.Locator(".secret")).ToBeHiddenAsync();
        (await Sayfa.ContentAsync()).ShouldNotContain(deger);
    }

    [Fact]
    public async Task Mobil_cekmece_menuyu_acar_ve_gezinince_kapanir()
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E Mobil");
        await GirisYapAsync(eposta, isyeri.Slug);

        await Sayfa.SetViewportSizeAsync(375, 812); // iPhone genişliği
        await Sayfa.GotoAsync($"{Uygulama.PanelAdres}/");

        var gezinme = Sayfa.GetByRole(AriaRole.Navigation, new() { Name = "Ana gezinme" });
        await Assertions.Expect(gezinme).ToBeHiddenAsync();

        await Sayfa.GetByLabel("Menüyü aç veya kapat").ClickAsync();
        await Assertions.Expect(gezinme).ToBeVisibleAsync();

        // Çekmece JS'siz: sayfa geçişi checkbox'ı sıfırladığı için menü kendiliğinden kapanır.
        await gezinme.GetByRole(AriaRole.Link, new() { Name = "Ödemeler" }).ClickAsync();
        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = "Ödemeler" }))
            .ToBeVisibleAsync();
        await Assertions.Expect(gezinme).ToBeHiddenAsync();
    }

    [Fact]
    public async Task Yazdirmada_gezinme_ve_butonlar_cikmaz()
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E Yazdır");
        await GirisYapAsync(eposta, isyeri.Slug);
        await Sayfa.GotoAsync($"{Uygulama.PanelAdres}/odemeler");

        var gezinme = Sayfa.GetByRole(AriaRole.Navigation, new() { Name = "Ana gezinme" });
        await Assertions.Expect(gezinme).ToBeVisibleAsync();

        // Print stilleri yalnız yazdırma kipinde devreye girer; hiçbir HTTP testi bunu
        // göremez. Muhasebeciye verilen çıktıda menü ve "Filtrele" düğmesi olmamalı.
        await Sayfa.EmulateMediaAsync(new PageEmulateMediaOptions { Media = Media.Print });

        await Assertions.Expect(gezinme).ToBeHiddenAsync();
        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Button, new() { Name = "Filtrele" }))
            .ToBeHiddenAsync();
        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = "Ödemeler" }))
            .ToBeVisibleAsync(); // içerik kalmalı
    }

    /// <summary>
    /// Kapsam ölçümünde %0 çıkan ekranlar. Hiçbir test bunları YÜKLEMİYORDU: bir null
    /// başvurusu ya da bozuk bir Fmt çağrısı sahaya kadar gidebilirdi. Burada iddia
    /// mütevazı ama değerli — sayfa açılıyor, başlığı basılıyor ve hata sayfası değil.
    /// </summary>
    [Theory]
    [InlineData("/alacaklar", "Bankalardan alacak")]
    [InlineData("/uyum", "Uyum ve denetim")]
    [InlineData("/risk", "Risk kuralları")]
    [InlineData("/musteriler", "Müşteriler")]
    [InlineData("/guvenlik", "Güvenlik")]
    public async Task Ekran_hatasiz_aciliyor(string yol, string baslik)
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E Ekran");
        await GirisYapAsync(eposta, isyeri.Slug);

        var konsolHatalari = new List<string>();
        Sayfa.Console += (_, ileti) =>
        {
            if (ileti.Type == "error") konsolHatalari.Add(ileti.Text);
        };

        var yanit = await Sayfa.GotoAsync($"{Uygulama.PanelAdres}{yol}");

        yanit!.Status.ShouldBe(200);
        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = baslik }))
            .ToBeVisibleAsync();
        konsolHatalari.ShouldBeEmpty();
    }
}
