using Microsoft.Playwright;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Poyra.Tests.E2E;

/// <summary>
/// Panelin iki interaktif adası (@rendermode InteractiveServer). Bunlar HTTP seviyesinde
/// SINANAMAZ: statik SSR yanıtı butonları ve tabloyu zaten basar, ama davranış SignalR
/// devresi kurulduktan sonra başlar. Entegrasyon testi HTML'i görüp "çalışıyor" sanır;
/// devre hiç kurulmasa da o test yeşil kalır.
/// </summary>
public sealed class InteraktifAdalarTests(PoyraAppFixture uygulama, ITestOutputHelper cikti)
    : E2ETest(uygulama, cikti)
{
    [Fact]
    public async Task Rota_tasarimcisi_kurali_iki_asamada_yayina_alir()
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E Rota");
        await GirisYapAsync(eposta, isyeri.Slug);

        await Sayfa.GotoAsync($"{Uygulama.PanelAdres}/rota");
        await Assertions.Expect(Sayfa.GetByText("Aktif kural yok")).ToBeVisibleAsync();

        // Kip değiştirme sunucuda koşan bir @onclick'tir: JSON alanı geldiyse devre canlıdır.
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "JSON" }).ClickAsync();
        var taslak = Sayfa.GetByLabel("Kural (JSON)");
        await Assertions.Expect(taslak).ToBeVisibleAsync();

        await taslak.FillAsync("""{ "strategy": "cheapest" }""");
        await Sayfa.GetByLabel("Kural adı").FillAsync("E2E en ucuz");

        // İlk tık YAYINLAMAZ, yalnız onay bloğunu açar — iki aşamalı yayın deseni.
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Yayına al" }).ClickAsync();
        await Assertions.Expect(Sayfa.GetByText("Simüle etmeden yayınlıyorsunuz")).ToBeVisibleAsync();
        await Assertions.Expect(Sayfa.GetByText("Aktif kural yok")).ToBeVisibleAsync();

        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Onayla ve yayına al" }).ClickAsync();

        await Assertions.Expect(Sayfa.GetByText("Aktif kural yok")).ToBeHiddenAsync();
        await Assertions.Expect(Sayfa.Locator(".reason")).ToContainTextAsync("E2E en ucuz");
    }

    [Fact]
    public async Task Olcum_kotasi_alani_yalniz_olculen_sinyalli_stratejilerde_gorunur()
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E Kota");
        await GirisYapAsync(eposta, isyeri.Slug);

        await Sayfa.GotoAsync($"{Uygulama.PanelAdres}/rota");
        await Assertions.Expect(Sayfa.GetByText("Aktif kural yok")).ToBeVisibleAsync();

        // Önce devrenin kurulduğunu KANITLA: statik SSR select'i zaten basar, ama hidrasyon
        // bitmeden seçim yapmak olayı boşluğa gönderir (sunucu hiç haberdar olmaz) ve test
        // "alan görünmedi" diye yanlış yerden kırılır. Kip düğmesi sunucuda koşan bir
        // @onclick — JSON alanı geldiyse devre canlıdır. Geri dönüş ayrıca JSON gidiş-dönüşünü
        // de sınar: kota alanı ToJson/FromJson'da düşerse burada yakalanır.
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "JSON" }).ClickAsync();
        await Assertions.Expect(Sayfa.GetByLabel("Kural (JSON)")).ToBeVisibleAsync();
        await Sayfa.GetByRole(AriaRole.Button, new() { Name = "Görsel kurucu" }).ClickAsync();

        var strateji = Sayfa.GetByLabel("Kural eşleşmezse");
        await Assertions.Expect(strateji).ToBeVisibleAsync();

        // Varsayılan taslak "cheapest" — anlaşma oranı trafikten beslenmez, kota gizli
        var kota = Sayfa.GetByLabel("Ölçüm kotası (%)");
        await Assertions.Expect(kota).ToBeHiddenAsync();

        // Ölçülen sinyale geçince alan açılmalı ve varsayılan kotayı taşımalı
        await strateji.SelectOptionAsync("best_success");
        await Assertions.Expect(kota).ToBeVisibleAsync();
        await Assertions.Expect(kota).ToHaveValueAsync("10");

        // Anlaşma oranına dayanan stratejide yeniden gizlenmeli
        await strateji.SelectOptionAsync("cheapest");
        await Assertions.Expect(kota).ToBeHiddenAsync();
    }

    [Fact]
    public async Task Canli_akis_yeni_odemeyi_sayfa_yenilenmeden_gosterir()
    {
        var (isyeri, eposta) = await Uygulama.IsyeriKurAsync("E2E Canlı");
        await GirisYapAsync(eposta, isyeri.Slug);

        // Rozet "bağlanıyor…" → "canlı" olduğunda hem devre hem Postgres LISTEN hazırdır.
        await Assertions.Expect(Sayfa.Locator(".live-badge")).ToContainTextAsync("canlı");

        var gezinme = Sayfa.Url;

        // Ödeme DIŞARIDAN gelir (API üzerinden): Api NOTIFY gönderir, panel LISTEN eder.
        await Uygulama.OdemeYapAsync(isyeri.ApiKey, 199_00);

        // Hiçbir yenileme/tıklama yok — satır kendiliğinden düşmeli.
        await Assertions.Expect(Sayfa.GetByText("199,00").First)
            .ToBeVisibleAsync(new() { Timeout = 15_000 });
        Sayfa.Url.ShouldBe(gezinme);
    }
}
