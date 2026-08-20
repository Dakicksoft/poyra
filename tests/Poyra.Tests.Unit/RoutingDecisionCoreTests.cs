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
