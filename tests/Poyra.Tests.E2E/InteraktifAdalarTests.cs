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
