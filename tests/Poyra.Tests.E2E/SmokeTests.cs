using Microsoft.Playwright;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Poyra.Tests.E2E;

/// <summary>
/// E2E duman testleri: iskelet gerçekten çalışıyor mu? Üç senaryo, üç farklı boru hattı —
/// kimlik kapısı (Panel), form + antiforgery belirteci (Panel), müşteriye bakan ödeme akışı
/// (Checkout → banka formu → Api callback → sonuç sayfası).
///
/// Bu katmanın entegrasyon testlerinden farkı: burada CSS yükleniyor, JS çalışıyor,
/// tarayıcı formu gerçekten gönderiyor. Entegrasyon testi HTML'i görür, tarayıcıyı görmez.
/// </summary>
public sealed class SmokeTests(PoyraAppFixture uygulama, ITestOutputHelper cikti)
    : E2ETest(uygulama, cikti)
{
    [Fact]
    public async Task Kimliksiz_kullanici_korumali_sayfaya_giremez()
    {
        await Sayfa.GotoAsync("/odemeler");

        // Statik SSR'de NotAuthorized dalı giriş formunu basar — sayfa 200 döner ama
        // içerik ödemeler DEĞİLDİR. Kullanıcının gördüğü şeyi doğruluyoruz.
        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = "Giriş yapın" }))
            .ToBeVisibleAsync();
        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = "Ödemeler" }))
            .ToBeHiddenAsync();
    }

    [Fact]
    public async Task Giris_yapan_kullanici_panoyu_gorur()
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E Duman");

        await GirisYapAsync(eposta, isyeri.Slug);

        // Antiforgery açık: belirteç formda gerçekten üretilmemiş olsaydı burada 400 alırdık.
        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = "Genel Bakış" }))
            .ToBeVisibleAsync();

        // Stil gerçekten yüklendi mi? MapStaticAssets parmak izli adres üretir; bozulursa
        // sayfa çalışır ama düzen dağılır — entegrasyon testinin göremediği tam olarak bu.
        var arkaPlan = await Sayfa.Locator("body").EvaluateAsync<string>(
            "el => getComputedStyle(el).backgroundColor");
        arkaPlan.ShouldNotBe("rgba(0, 0, 0, 0)");
    }

    [Fact]
    public async Task Odeme_baglantisi_tarayicidan_odenir_ve_panelde_gorunur()
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E Bağlantı");

        // 149,00 ₺ — mockbank kuruş %100 == 99/98 dışını onaylar (bkz. MockBankConnector)
        var baglanti = await Uygulama.OdemeBaglantisiOlusturAsync(isyeri.ApiKey, new
        {
            description = "E2E sipariş",
            amountMinor = 149_00,
        });

        // --- Müşteri tarafı: checkout ---
        await Sayfa.GotoAsync($"{Uygulama.CheckoutAdres}/l/{baglanti.Slug}");
        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = "E2E sipariş" }))
            .ToBeVisibleAsync();
        await Assertions.Expect(Sayfa.Locator(".amount")).ToContainTextAsync("149,00");

        // Tıklamadan sonra: checkout banka formunu döner, form KENDİ KENDİNE gönderilir,
        // Api callback'i sonucu işler ve sonuç sayfasına yönlendirir. Bu zincirin
        // tamamı tarayıcıda koşar — otomatik gönderim JS'i burada gerçekten çalışır.
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Güvenli ödemeye geç" }).ClickAsync();

        await Assertions.Expect(Sayfa.GetByRole(AriaRole.Heading, new() { Name = "Ödemeniz alındı" }))
            .ToBeVisibleAsync(new() { Timeout = 15_000 });
        Sayfa.Url.ShouldContain($"/l/{baglanti.Slug}/sonuc");

        // --- İşyeri tarafı: aynı ödeme panelde görünmeli ---
        await GirisYapAsync(eposta, isyeri.Slug);
        await Sayfa.GotoAsync($"{Uygulama.PanelAdres}/odemeler");
        await Assertions.Expect(Sayfa.GetByText("149,00").First).ToBeVisibleAsync();
    }
}
