using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Routing.Dsl;
using Poyra.Modules.Routing.Infrastructure;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Karar çekirdeği (RoutingEngine.DecideCore) — motor ve simülatörün ORTAK yolu.
/// Özellikle hacim bölüşümünün çalışma zamanı davranışı: kova seçimi deterministiktir,
/// yalnız hiçbir kural eşleşmediğinde devreye girer ve stratejiden önce gelir.
/// </summary>
public sealed class RoutingDecisionCoreTests
{
    // Ucuz ama düşük başarılı · pahalı ama yüksek başarılı · sinyalsiz üçüncü hesap
    private static readonly RoutingCandidate Ucuz = new(Guid.NewGuid(), "Ucuz POS", 200, 0.80, 900);
    private static readonly RoutingCandidate Pahali = new(Guid.NewGuid(), "Pahalı POS", 350, 0.97, 180);
    private static readonly RoutingCandidate Yedek = new(Guid.NewGuid(), "Yedek POS", 500, null, null);

    private static readonly RoutingCandidate[] Adaylar = [Ucuz, Pahali, Yedek];

    private static RoutingFacts Facts(Guid seed, long amount = 50_000)
        => new(seed, amount, "TRY", 1, 14);

    private static RuleDocument Doc(string json) => RuleDocument.Parse(json);

    /// <summary>Testler tekrar üretilebilsin diye sabit tohum: GUID'in ilk 4 baytı = i.</summary>
    private static Guid Seed(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    [Fact]
    public void Ayni_tohum_her_zaman_ayni_hesaba_dusmeli()
    {
        var doc = Doc("""
            { "volumeSplit": [ { "account": "Ucuz POS", "percent": 50 },
                               { "account": "Pahalı POS", "percent": 50 } ] }
            """);

        foreach (var i in Enumerable.Range(0, 20))
        {
            var once = RoutingEngine.DecideCore(doc, Facts(Seed(i)), Adaylar);
            var tekrar = RoutingEngine.DecideCore(doc, Facts(Seed(i)), Adaylar);

            tekrar.AccountIds[0].ShouldBe(once.AccountIds[0]); // karar tekrar oynatılabilir
            once.Reason.ShouldContain("Hacim bölüşümü");
        }
    }

    [Fact]
    public void Yuzde_yuz_tek_kovaya_verilirse_tum_tohumlar_o_hesaba_gitmeli()
    {
        var doc = Doc("""{ "volumeSplit": [ { "account": "Pahalı POS", "percent": 100 } ] }""");

        foreach (var i in Enumerable.Range(0, 50))
            RoutingEngine.DecideCore(doc, Facts(Seed(i)), Adaylar).AccountIds[0].ShouldBe(Pahali.AccountId);
    }

    [Fact]
    public void Sifir_yuzdeli_hesap_hic_trafik_almamali()
    {
        // Kümülatif kova mantığı: %0'lık ilk girişe hiçbir kova düşemez
        var doc = Doc("""
            { "volumeSplit": [ { "account": "Ucuz POS", "percent": 0 },
                               { "account": "Pahalı POS", "percent": 100 } ] }
            """);

        foreach (var i in Enumerable.Range(0, 50))
            RoutingEngine.DecideCore(doc, Facts(Seed(i)), Adaylar).AccountIds[0].ShouldBe(Pahali.AccountId);
    }

    [Fact]
    public void Elli_elli_bolusumde_iki_hesap_da_pay_almali()
    {
        var doc = Doc("""
            { "volumeSplit": [ { "account": "Ucuz POS", "percent": 50 },
                               { "account": "Pahalı POS", "percent": 50 } ] }
            """);

        var kazananlar = Enumerable.Range(0, 100)
            .Select(i => RoutingEngine.DecideCore(doc, Facts(Seed(i)), Adaylar).AccountIds[0])
            .ToHashSet();

        // Sabit 100 tohum SHA256 kovalarına dağılır — iki taraf da boş kalamaz
        kazananlar.ShouldContain(Ucuz.AccountId);
        kazananlar.ShouldContain(Pahali.AccountId);
    }

    [Fact]
    public void Kural_eslesirse_hacim_bolusumu_devreye_girmemeli()
    {
        // Bölüşüm %100 Ucuz dese de eşleşen kural kazanır — bölüşüm yalnız kuralsız işlemler için
        var doc = Doc("""
            { "rules": [ { "name": "yüksek tutar",
                           "when": { "fact": "amount_minor", "op": "gte", "value": 10000 },
                           "route": ["Pahalı POS"] } ],
              "volumeSplit": [ { "account": "Ucuz POS", "percent": 100 } ] }
            """);

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(7)), Adaylar);

        decision.AccountIds[0].ShouldBe(Pahali.AccountId);
        decision.Reason.ShouldContain("Kural eşleşti");
    }

    [Fact]
    public void Kural_eslesmeyince_bolusum_stratejiden_once_gelmeli()
    {
        // cheapest Ucuz'u seçerdi; ama bölüşüm tanımlıysa strateji ancak bölüşüm boşa düşerse konuşur
        var doc = Doc("""
            { "strategy": "cheapest",
              "rules": [ { "name": "gece", "when": { "fact": "hour", "op": "gte", "value": 22 },
                           "route": ["Ucuz POS"] } ],
              "volumeSplit": [ { "account": "Pahalı POS", "percent": 100 } ] }
            """);

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(3)), Adaylar); // saat 14 — kural eşleşmez

        decision.AccountIds[0].ShouldBe(Pahali.AccountId);
        decision.Reason.ShouldContain("Hacim bölüşümü");
    }

    [Fact]
    public void Bolusumdeki_bilinmeyen_hesap_atlanmali_ve_strateji_devreye_girmeli()
    {
        // Kapatılmış hesaba işaret eden bölüşüm sessizce boşa düşer, doküman stratejisi karar verir
        var doc = Doc("""
            { "strategy": "cheapest",
              "volumeSplit": [ { "account": "Kapatılmış POS", "percent": 100 } ] }
            """);

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(11)), Adaylar);

        decision.AccountIds[0].ShouldBe(Ucuz.AccountId);
        decision.Reason.ShouldContain("en düşük komisyon");
    }

    [Fact]
    public void Zincir_bolusum_kazananini_fallback_ve_kalanlarla_tamamlamali()
    {
        var doc = Doc("""
            { "volumeSplit": [ { "account": "Pahalı POS", "percent": 100 } ],
              "fallback": ["Yedek POS"] }
            """);

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(1)), Adaylar);

        // Failover sırası: bölüşüm kazananı → fallback → kalan uygun hesaplar
        decision.AccountIds.ShouldBe([Pahali.AccountId, Yedek.AccountId, Ucuz.AccountId]);
        decision.MaxAttempts.ShouldBe(2); // guards varsayılanı
    }

    [Fact]
    public void Kova_sozlesmesi_altin_degerlerle_sabitlenmeli()
    {
        // Kova = SHA256(seed.ToString("N")) ilk 4 bayt little-endian % 100 — değerler bağımsız
        // hesaplandı. Sözleşme değişirse (hash, endianness, tohum biçimi) canlıdaki A/B trafiği
        // sessizce yeniden dağılır ve kayıtlı kararlar tekrar oynatılamaz; bu test bunu pinler.
        var doc = Doc("""
            { "volumeSplit": [ { "account": "Ucuz POS", "percent": 50 },
                               { "account": "Pahalı POS", "percent": 50 } ] }
            """);

        var beklenen = new Dictionary<int, int> { [0] = 32, [1] = 11, [3] = 67, [7] = 66, [11] = 87 };
        foreach (var (tohum, kova) in beklenen)
        {
            var decision = RoutingEngine.DecideCore(doc, Facts(Seed(tohum)), Adaylar);

            decision.Reason.ShouldContain($"kova %{kova}");
            decision.AccountIds[0].ShouldBe(kova < 50 ? Ucuz.AccountId : Pahali.AccountId);
        }
    }
}

/// <summary>
/// Ölçüm kotası (guards.explorePercent) — "kazanan her şeyi alır" kilidinin çözümü.
/// Ölçülen sinyale dayanan stratejilerde (best_success · fastest · balanced) trafiğin bir
/// kısmı sinyali olmayan adaya ayrılır; aksi hâlde kaybeden hesap hiç örnek toplayamaz,
/// performans penceresi boşalınca sinyali ölür ve bir daha asla öne geçemez.
/// </summary>
public sealed class ExploreQuotaTests
{
    // Ölçülmüş iki hesap + sinyali ölmüş/hiç ölçülmemiş üçüncü
    private static readonly RoutingCandidate Ucuz = new(Guid.NewGuid(), "Ucuz POS", 200, 0.80, 900);
    private static readonly RoutingCandidate Pahali = new(Guid.NewGuid(), "Pahalı POS", 350, 0.97, 180);
    private static readonly RoutingCandidate Sinyalsiz = new(Guid.NewGuid(), "Sinyalsiz POS", 500, null, null);

    private static readonly RoutingCandidate[] Adaylar = [Ucuz, Pahali, Sinyalsiz];

    private static RoutingFacts Facts(Guid seed) => new(seed, 50_000, "TRY", 1, 14);

    private static RuleDocument Doc(string json) => RuleDocument.Parse(json);

    private static Guid Seed(int i) => new(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    /// <summary>Varsayılan %10 kotada keşfe düşen tohum (kova %5).</summary>
    private const int KotayaDusen = 18;

    /// <summary>Varsayılan %10 kotanın dışında kalan tohum (kova %55).</summary>
    private const int KotaDisi = 1;

    [Fact]
    public void Kotaya_dusen_istek_sinyalsiz_adayi_one_almali()
    {
        var doc = Doc("""{ "strategy": "best_success" }""");

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(KotayaDusen)), Adaylar);

        decision.AccountIds[0].ShouldBe(Sinyalsiz.AccountId);
        decision.Reason.ShouldContain("Ölçüm kotası");
        decision.Reason.ShouldContain("Sinyalsiz POS");
        decision.Reason.ShouldContain("kova %5 < %10");

        // Strateji kazananı HEMEN ARKADA kalmalı: ölçüm denemesi başarısız olursa
        // failover en iyi hesabı yakalar — kota tahsilatı riske atmaz
        decision.AccountIds[1].ShouldBe(Pahali.AccountId);
    }

    [Fact]
    public void Kota_disindaki_istekler_normal_strateji_sirasiyla_gitmeli()
    {
        var doc = Doc("""{ "strategy": "best_success" }""");

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(KotaDisi)), Adaylar);

        decision.AccountIds[0].ShouldBe(Pahali.AccountId); // %97 başarı
        decision.Reason.ShouldContain("en yüksek başarı oranı");
        decision.Reason.ShouldNotContain("Ölçüm kotası");
    }

    [Fact]
    public void Kilitlenme_cozulmeli_sinyalsiz_hesap_paini_almali_ama_azinlikta_kalmali()
    {
        // Kusurun kendisi: kota olmadan Sinyalsiz POS bu 1.000 isteğin HİÇBİRİNİ alamazdı,
        // dolayısıyla ölçülemez ve sonsuza dek sonda kalırdı.
        var kotali = Doc("""{ "strategy": "best_success" }""");
        var kotasiz = Doc("""{ "strategy": "best_success", "guards": { "explorePercent": 0 } }""");

        var kotaliKazananlar = Enumerable.Range(0, 1_000)
            .Select(i => RoutingEngine.DecideCore(kotali, Facts(Seed(i)), Adaylar).AccountIds[0])
            .ToList();
        var kotasizKazananlar = Enumerable.Range(0, 1_000)
            .Select(i => RoutingEngine.DecideCore(kotasiz, Facts(Seed(i)), Adaylar).AccountIds[0])
            .ToList();

        kotasizKazananlar.ShouldAllBe(id => id == Pahali.AccountId); // kilit: tek kazanan

        var olcumPayi = kotaliKazananlar.Count(id => id == Sinyalsiz.AccountId);
        olcumPayi.ShouldBeInRange(80, 150); // ~%10 hedefi; 1.000 tohumda 116
        kotaliKazananlar.Count(id => id == Pahali.AccountId).ShouldBe(1_000 - olcumPayi);
    }

    [Fact]
    public void Kota_sifirlanirsa_hic_kesif_yapilmamali()
    {
        var doc = Doc("""{ "strategy": "best_success", "guards": { "explorePercent": 0 } }""");

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(KotayaDusen)), Adaylar);

        decision.AccountIds[0].ShouldBe(Pahali.AccountId);
        decision.Reason.ShouldNotContain("Ölçüm kotası");
    }

    [Fact]
    public void Olculen_sinyale_dayanmayan_stratejide_kota_calismamali()
    {
        // cheapest anlaşma oranını okur — trafik akmasa da bayatlamaz, ölçüme ihtiyacı yok
        var doc = Doc("""{ "strategy": "cheapest" }""");

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(KotayaDusen)), Adaylar);

        decision.AccountIds[0].ShouldBe(Ucuz.AccountId);
        decision.Reason.ShouldNotContain("Ölçüm kotası");
    }

    [Fact]
    public void Kural_sabit_rota_verdiyse_kota_onu_ezmemeli()
    {
        // İşyeri "bu işlem şu POS'a gitsin" demiş — ölçüm merakı açık talimatı ezemez
        var doc = Doc("""
            { "strategy": "best_success",
              "rules": [ { "name": "yüksek tutar",
                           "when": { "fact": "amount_minor", "op": "gte", "value": 10000 },
                           "route": ["Pahalı POS"] } ] }
            """);

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(KotayaDusen)), Adaylar);

        decision.AccountIds[0].ShouldBe(Pahali.AccountId);
        decision.Reason.ShouldContain("Kural eşleşti");
        decision.Reason.ShouldNotContain("Ölçüm kotası");
    }

    [Fact]
    public void Kural_yalniz_strateji_verdiyse_kota_calismali()
    {
        // Sabit rota yok, sıralamayı strateji kurdu — kota devrede
        var doc = Doc("""
            { "rules": [ { "name": "gündüz",
                           "when": { "fact": "hour", "op": "lt", "value": 22 },
                           "strategy": "best_success" } ] }
            """);

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(KotayaDusen)), Adaylar);

        decision.AccountIds[0].ShouldBe(Sinyalsiz.AccountId);
        decision.Reason.ShouldContain("Ölçüm kotası");
    }

    [Fact]
    public void Hacim_bolusumu_kotayi_devre_disi_birakmali()
    {
        // Bölüşüm zaten işyerinin kendi A/B düzeneği — üstüne kota bindirmek onu bozar
        var doc = Doc("""
            { "strategy": "best_success",
              "volumeSplit": [ { "account": "Ucuz POS", "percent": 100 } ] }
            """);

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(KotayaDusen)), Adaylar);

        decision.AccountIds[0].ShouldBe(Ucuz.AccountId);
        decision.Reason.ShouldContain("Hacim bölüşümü");
        decision.Reason.ShouldNotContain("Ölçüm kotası");
    }

    [Fact]
    public void Tum_adaylar_olculmusse_kota_harcanmamali()
    {
        var doc = Doc("""{ "strategy": "best_success" }""");

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(KotayaDusen)), [Ucuz, Pahali]);

        decision.AccountIds[0].ShouldBe(Pahali.AccountId);
        decision.Reason.ShouldNotContain("Ölçüm kotası");
    }

    [Fact]
    public void Sinyalsiz_aday_zaten_bastaysa_kota_harcanmamali()
    {
        // balanced, sinyalsiz adayı maliyeti sayesinde zaten başa koyuyor: nasılsa denenecek
        // ve ölçülecek. Kotayı harcamanın anlamı yok — sıra olduğu gibi kalmalı.
        var ucuzSinyalsiz = new RoutingCandidate(Guid.NewGuid(), "Ucuz Sinyalsiz POS", 100, null, null);
        var pahaliOlculmus = new RoutingCandidate(Guid.NewGuid(), "Pahalı Ölçülmüş POS", 900, 0.60, 500);

        var doc = Doc("""{ "strategy": "balanced" }""");

        var decision = RoutingEngine.DecideCore(
            doc, Facts(Seed(KotayaDusen)), [ucuzSinyalsiz, pahaliOlculmus]);

        decision.AccountIds[0].ShouldBe(ucuzSinyalsiz.AccountId);
        decision.Reason.ShouldContain("dengeli");
        decision.Reason.ShouldNotContain("Ölçüm kotası");
    }

    [Fact]
    public void Ayni_tohum_ayni_kesif_kararini_vermeli()
    {
        var doc = Doc("""{ "strategy": "best_success" }""");

        foreach (var i in Enumerable.Range(0, 30))
        {
            var once = RoutingEngine.DecideCore(doc, Facts(Seed(i)), Adaylar);
            var tekrar = RoutingEngine.DecideCore(doc, Facts(Seed(i)), Adaylar);

            tekrar.AccountIds.ShouldBe(once.AccountIds); // simülatör motorla aynı sonucu görür
            tekrar.Reason.ShouldBe(once.Reason);
        }
    }

    [Fact]
    public void Kota_kovasi_hacim_bolusumunun_kovasindan_bagimsiz_olmali()
    {
        // Kota kovası "explore:" ile tuzlanır. Tuz olmasaydı "%10'luk kotaya düşen istek"
        // ile "%10'luk bölüşüm kovası" HEP AYNI işlemler olurdu — iki bağımsız karar
        // birbirine kilitlenirdi. Altın değerler bağımsız hesaplandı; sözleşme (hash,
        // endianness, tuz metni) değişirse canlıdaki ölçüm trafiği sessizce yeniden dağılır.
        var kota = Doc("""{ "strategy": "best_success" }""");
        var bolusum = Doc("""
            { "volumeSplit": [ { "account": "Ucuz POS", "percent": 50 },
                               { "account": "Pahalı POS", "percent": 50 } ] }
            """);

        var beklenen = new Dictionary<int, (int Kota, int Bolusum)>
        {
            [0] = (10, 32), [1] = (55, 11), [18] = (5, 95), [20] = (5, 23),
        };

        foreach (var (tohum, (kotaKova, bolusumKova)) in beklenen)
        {
            RoutingEngine.DecideCore(bolusum, Facts(Seed(tohum)), Adaylar)
                .Reason.ShouldContain($"kova %{bolusumKova}");

            // Kota kovası yalnız eşiğin altına düştüğünde gerekçeye yazılır
            if (kotaKova < 10)
                RoutingEngine.DecideCore(kota, Facts(Seed(tohum)), Adaylar)
                    .Reason.ShouldContain($"kova %{kotaKova} < %10");
            else
                RoutingEngine.DecideCore(kota, Facts(Seed(tohum)), Adaylar)
                    .Reason.ShouldNotContain("Ölçüm kotası");
        }
    }

    [Fact]
    public void Deneme_hakki_tekse_kota_kapanmali()
    {
        // maxAttempts=1 → keşif denemesinin arkasında failover yok. Ölçüm için sinyalsiz
        // POS'a yönlendirmek, kurtarma olmadan tahsilatı kumara çevirirdi.
        var doc = Doc("""{ "strategy": "best_success", "guards": { "maxAttempts": 1 } }""");

        var decision = RoutingEngine.DecideCore(doc, Facts(Seed(KotayaDusen)), Adaylar);

        decision.AccountIds[0].ShouldBe(Pahali.AccountId);
        decision.Reason.ShouldNotContain("Ölçüm kotası");
    }

    [Fact]
    public void Kota_ust_sinirla_kirpilmali()
    {
        // %100 kota isteyen doküman stratejiyi tamamen anlamsızlaştırırdı — tavan %50
        var doc = Doc("""{ "strategy": "best_success", "guards": { "explorePercent": 100 } }""");

        // Kova %55 olan tohum: kırpılmamış olsaydı (%100) keşfe düşerdi, %50 tavanında düşmez
        RoutingEngine.DecideCore(doc, Facts(Seed(KotaDisi)), Adaylar)
            .AccountIds[0].ShouldBe(Pahali.AccountId);

        // Tavanın altındaki kova ise keşfe düşer ve gerekçede kırpılmış değer görünür
        RoutingEngine.DecideCore(doc, Facts(Seed(KotayaDusen)), Adaylar)
            .Reason.ShouldContain("kova %5 < %50");
    }
}

/// <summary>Uygunluk elemesi — motor ve simülatörün ortak süzgeci (RoutingEngine.FilterEligible).</summary>
public sealed class FilterEligibleTests
{
    private static ConnectorAccountSnapshot Hesap(string label, ConnectorHealth health)
        => new(Guid.NewGuid(), "mockbank", label, Priority: 1, TestMode: false, health, Active: true);

    [Fact]
    public void Down_hesap_skipUnhealthy_kapali_olsa_bile_elenmelidir()
    {
        var eligible = RoutingEngine.FilterEligible(
            [Hesap("A", ConnectorHealth.Down), Hesap("B", ConnectorHealth.Healthy)],
            new RuleGuards { SkipUnhealthy = false });

        eligible.Select(a => a.Label).ShouldBe(["B"]);
    }

    [Fact]
    public void SkipUnhealthy_varsayilaninda_degraded_hesap_da_elenmelidir()
    {
        var eligible = RoutingEngine.FilterEligible(
            [Hesap("A", ConnectorHealth.Degraded), Hesap("B", ConnectorHealth.Healthy)],
            new RuleGuards()); // varsayılan: skipUnhealthy = true

        eligible.Select(a => a.Label).ShouldBe(["B"]);
    }

    [Fact]
    public void SkipUnhealthy_kapaliyken_degraded_hesap_rotada_kalmali()
    {
        var eligible = RoutingEngine.FilterEligible(
            [Hesap("A", ConnectorHealth.Degraded), Hesap("B", ConnectorHealth.Healthy)],
            new RuleGuards { SkipUnhealthy = false });

        eligible.Select(a => a.Label).ShouldBe(["A", "B"]);
    }
}
