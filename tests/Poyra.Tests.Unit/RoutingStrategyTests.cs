using System.Text.Json;
using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Routing.Dsl;
using Poyra.Modules.Routing.Infrastructure;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class RoutingStrategyTests
{
    // Ucuz ama yavaş/başarısız · pahalı ama hızlı/başarılı · sinyalsiz (yeni POS)
    private static readonly RoutingCandidate Ucuz = new(Guid.NewGuid(), "Ucuz POS", 200, 0.80, 900);
    private static readonly RoutingCandidate Hizli = new(Guid.NewGuid(), "Hızlı POS", 350, 0.97, 180);
    private static readonly RoutingCandidate Yeni = new(Guid.NewGuid(), "Yeni POS", null, null, null);

    private static readonly RoutingCandidate[] Adaylar = [Ucuz, Hizli, Yeni];

    [Fact]
    public void Cheapest_en_dusuk_komisyonu_one_almali_sinyalsizi_sona()
    {
        var ordered = RoutingStrategies.Order(Adaylar, RoutingStrategies.Cheapest, new StrategyWeights());

        ordered[0].Label.ShouldBe("Ucuz POS");
        ordered[1].Label.ShouldBe("Hızlı POS");
        ordered[2].Label.ShouldBe("Yeni POS"); // maliyeti bilinmeyen elenmez, sona alınır
    }

    [Fact]
    public void BestSuccess_en_yuksek_basari_oranini_one_almali()
        => RoutingStrategies.Order(Adaylar, RoutingStrategies.BestSuccess, new StrategyWeights())[0]
            .Label.ShouldBe("Hızlı POS");

    [Fact]
    public void Fastest_en_dusuk_gecikmeyi_one_almali()
        => RoutingStrategies.Order(Adaylar, RoutingStrategies.Fastest, new StrategyWeights())[0]
            .Label.ShouldBe("Hızlı POS");

    [Fact]
    public void Priority_gelen_sirayi_korumali()
    {
        var ordered = RoutingStrategies.Order(Adaylar, RoutingStrategies.Priority, new StrategyWeights());
        ordered.Select(c => c.Label).ShouldBe(["Ucuz POS", "Hızlı POS", "Yeni POS"]);
    }

    [Fact]
    public void Balanced_agirliga_gore_kazananı_degistirmeli()
    {
        // Maliyet ağırlıklıysa ucuz POS kazanır
        var costHeavy = RoutingStrategies.Order(Adaylar, RoutingStrategies.Balanced,
            new StrategyWeights { Cost = 5, Success = 1, Latency = 0 });
        costHeavy[0].Label.ShouldBe("Ucuz POS");

        // Başarı ağırlıklıysa hızlı/başarılı POS kazanır
        var successHeavy = RoutingStrategies.Order(Adaylar, RoutingStrategies.Balanced,
            new StrategyWeights { Cost = 1, Success = 5, Latency = 1 });
        successHeavy[0].Label.ShouldBe("Hızlı POS");
    }

    [Fact]
    public void Bilinmeyen_strateji_priority_gibi_davranmali()
    {
        RoutingStrategies.IsKnown("saçma").ShouldBeFalse();
        RoutingStrategies.Order(Adaylar, "saçma", new StrategyWeights())
            .Select(c => c.Label).ShouldBe(["Ucuz POS", "Hızlı POS", "Yeni POS"]);
    }

    [Fact]
    public void Yalniz_olculen_sinyale_dayanan_stratejiler_kotaya_girmeli()
    {
        // Trafikten beslenen sinyaller: başarı oranı ve gecikme
        RoutingStrategies.UsesMeasuredSignals(RoutingStrategies.BestSuccess).ShouldBeTrue();
        RoutingStrategies.UsesMeasuredSignals(RoutingStrategies.Fastest).ShouldBeTrue();
        RoutingStrategies.UsesMeasuredSignals(RoutingStrategies.Balanced).ShouldBeTrue();

        // cheapest yapılandırılmış anlaşma oranını, priority hesap önceliğini okur:
        // trafik akmasa da değerleri bayatlamaz, kota harcamaları gereksiz olurdu
        RoutingStrategies.UsesMeasuredSignals(RoutingStrategies.Cheapest).ShouldBeFalse();
        RoutingStrategies.UsesMeasuredSignals(RoutingStrategies.Priority).ShouldBeFalse();
    }

    [Fact]
    public void Sinyalsizlik_stratejinin_baktigi_boyuta_gore_belirlenmeli()
    {
        var yalnizHizi_olculmus = new RoutingCandidate(Guid.NewGuid(), "Yarım POS", 300, null, 250);

        // best_success başarı oranına bakar — gecikmesi ölçülmüş olması onu ölçülmüş yapmaz
        RoutingStrategies.IsUnmeasured(yalnizHizi_olculmus, RoutingStrategies.BestSuccess).ShouldBeTrue();
        RoutingStrategies.IsUnmeasured(yalnizHizi_olculmus, RoutingStrategies.Fastest).ShouldBeFalse();

        // balanced iki sinyali de kullanır: biri eksikse ölçülmeye muhtaçtır
        RoutingStrategies.IsUnmeasured(yalnizHizi_olculmus, RoutingStrategies.Balanced).ShouldBeTrue();

        RoutingStrategies.IsUnmeasured(Hizli, RoutingStrategies.Balanced).ShouldBeFalse();
        RoutingStrategies.IsUnmeasured(Yeni, RoutingStrategies.BestSuccess).ShouldBeTrue();
    }
}

public sealed class CardFactRuleTests
{
    private static RoutingFacts Facts(CardFacts? card, long amount = 50_000)
        => new(Guid.NewGuid(), amount, "TRY", 1, 14, card);

    private static readonly CardFacts BonusKarti =
        new("540061", "0062", "bonus", "mastercard", "credit", false);

    private static RuleDocument Doc(string json) => RuleDocument.Parse(json);

    [Fact]
    public void Kart_programina_gore_yonlendirme_eslesmeli()
    {
        var doc = Doc("""
            { "rules": [ { "name": "bonus-garanti",
                           "when": { "fact": "card.program", "op": "eq", "value": "bonus" },
                           "route": ["Garanti POS"] } ] }
            """);

        RuleEvaluator.FirstMatch(doc, Facts(BonusKarti))!.Name.ShouldBe("bonus-garanti");
        RuleEvaluator.FirstMatch(doc,
            Facts(BonusKarti with { Program = "world" })).ShouldBeNull();
    }

    [Fact]
    public void On_us_kurali_banka_koduyla_eslesmeli()
    {
        var doc = Doc("""
            { "rules": [ { "name": "on-us",
                "when": { "all": [
                    { "fact": "card.bank", "op": "eq", "value": "0062" },
                    { "fact": "amount_minor", "op": "gte", "value": 10000 } ] },
                "route": ["Garanti POS"], "reason": "on-us: kart bankası = POS bankası" } ] }
            """);

        RuleEvaluator.FirstMatch(doc, Facts(BonusKarti))!.Reason.ShouldContain("on-us");
        RuleEvaluator.FirstMatch(doc, Facts(BonusKarti with { BankCode = "0064" })).ShouldBeNull();
    }

    [Fact]
    public void Kart_tipi_ve_ticari_kart_kurallari()
    {
        var debit = Doc("""
            { "rules": [ { "when": { "fact": "card.type", "op": "eq", "value": "debit" }, "route": ["A"] } ] }
            """);
        RuleEvaluator.FirstMatch(debit, Facts(BonusKarti with { CardType = "debit" })).ShouldNotBeNull();
        RuleEvaluator.FirstMatch(debit, Facts(BonusKarti)).ShouldBeNull();

        var commercial = Doc("""
            { "rules": [ { "when": { "fact": "card.commercial", "op": "eq", "value": true }, "route": ["B"] } ] }
            """);
        RuleEvaluator.FirstMatch(commercial, Facts(BonusKarti with { IsCommercial = true })).ShouldNotBeNull();
        RuleEvaluator.FirstMatch(commercial, Facts(BonusKarti)).ShouldBeNull();
    }

    [Fact]
    public void Bin_onekiyle_eslesme_yapilabilmeli()
    {
        var doc = Doc("""
            { "rules": [ { "when": { "fact": "bin", "op": "starts_with", "value": "5400" }, "route": ["A"] } ] }
            """);

        RuleEvaluator.FirstMatch(doc, Facts(BonusKarti)).ShouldNotBeNull();
        RuleEvaluator.FirstMatch(doc, Facts(BonusKarti with { Bin = "979200" })).ShouldBeNull();
    }

    [Fact]
    public void Kart_bilinmiyorsa_kart_kurallari_eslesmemeli()
    {
        // Hosted akışta müşteri kartı henüz girmemiştir — kural atlanır, strateji devreye girer
        var doc = Doc("""
            { "rules": [ { "when": { "fact": "card.program", "op": "eq", "value": "bonus" }, "route": ["A"] } ] }
            """);

        RuleEvaluator.FirstMatch(doc, Facts(card: null)).ShouldBeNull();
    }

    [Fact]
    public void Marka_ve_in_operatoru_calismali()
    {
        var doc = Doc("""
            { "rules": [ { "when": { "fact": "card.brand", "op": "in", "value": ["troy","mastercard"] },
                           "route": ["A"] } ] }
            """);

        RuleEvaluator.FirstMatch(doc, Facts(BonusKarti)).ShouldNotBeNull();
        RuleEvaluator.FirstMatch(doc, Facts(BonusKarti with { Brand = "visa" })).ShouldBeNull();
    }

    [Fact]
    public void Kanal_kurali_eslesmeli()
    {
        var doc = Doc("""
            { "rules": [ { "name": "saha",
                           "when": { "fact": "channel", "op": "eq", "value": "field" },
                           "route": ["Saha POS"], "reason": "saha tahsilatı → Saha POS" } ] }
            """);

        RuleEvaluator.FirstMatch(doc, FactsWithChannel("field"))!.Name.ShouldBe("saha");
        RuleEvaluator.FirstMatch(doc, FactsWithChannel("link")).ShouldBeNull();
        RuleEvaluator.FirstMatch(doc, FactsWithChannel("api")).ShouldBeNull();
    }

    [Fact]
    public void Kanal_in_operatoruyle_birden_cok_deger_alabilmeli()
    {
        // "Müşteri ekranda değil" grubu: abonelik yenilemesi ve saha tahsilatı
        var doc = Doc("""
            { "rules": [ { "when": { "fact": "channel", "op": "in", "value": ["subscription","field"] },
                           "strategy": "best_success" } ] }
            """);

        RuleEvaluator.FirstMatch(doc, FactsWithChannel("subscription")).ShouldNotBeNull();
        RuleEvaluator.FirstMatch(doc, FactsWithChannel("field")).ShouldNotBeNull();
        RuleEvaluator.FirstMatch(doc, FactsWithChannel("api")).ShouldBeNull();
    }

    [Fact]
    public void Kanal_bilinmiyorsa_kanal_kurallari_eslesmemeli()
    {
        // Kanal alanı eklenmeden önceki kayıtlar: null gelir. "api sayalım" deseydik,
        // geçmişteki ödeme linkleri de api kuralına takılır ve simülatör yanlış raporlardı.
        var doc = Doc("""
            { "rules": [ { "when": { "fact": "channel", "op": "eq", "value": "api" }, "route": ["A"] } ] }
            """);

        RuleEvaluator.FirstMatch(doc, FactsWithChannel(null)).ShouldBeNull();
    }

    [Fact]
    public void Kanal_ve_kart_kosullari_birlikte_kullanilabilmeli()
    {
        var doc = Doc("""
            { "rules": [ { "name": "abonelik-ticari",
                "when": { "all": [
                    { "fact": "channel", "op": "eq", "value": "subscription" },
                    { "fact": "card.commercial", "op": "eq", "value": true } ] },
                "route": ["Kurumsal POS"] } ] }
            """);

        var ticari = BonusKarti with { IsCommercial = true };

        RuleEvaluator.FirstMatch(doc, Facts(ticari) with { Channel = "subscription" }).ShouldNotBeNull();
        RuleEvaluator.FirstMatch(doc, Facts(ticari) with { Channel = "link" }).ShouldBeNull();
        RuleEvaluator.FirstMatch(doc, Facts(BonusKarti) with { Channel = "subscription" }).ShouldBeNull();
    }

    [Fact]
    public void Bilinen_kanal_listesi_sozlesmeyi_korumali()
    {
        // Panel kataloğu, checkout eşlemesi ve kural DSL'i aynı dört değeri konuşur
        PaymentChannels.IsKnown(PaymentChannels.Api).ShouldBeTrue();
        PaymentChannels.IsKnown(PaymentChannels.Link).ShouldBeTrue();
        PaymentChannels.IsKnown(PaymentChannels.Field).ShouldBeTrue();
        PaymentChannels.IsKnown(PaymentChannels.Subscription).ShouldBeTrue();

        PaymentChannels.IsKnown(null).ShouldBeFalse();
        PaymentChannels.IsKnown("checkout").ShouldBeFalse(); // checkout kanal değil, linkin görüldüğü yer
    }

    private static RoutingFacts FactsWithChannel(string? channel)
        => new(Guid.NewGuid(), 50_000, "TRY", 1, 14, Card: null, Channel: channel);

    [Fact]
    public void Strateji_dokumanda_ve_kuralda_tanimlanabilmeli()
    {
        var doc = Doc("""
            { "strategy": "cheapest",
              "weights": { "cost": 2, "success": 3, "latency": 1 },
              "rules": [ { "name": "gece", "when": { "fact": "hour", "op": "gte", "value": 22 },
                           "strategy": "best_success" } ] }
            """);

        doc.Strategy.ShouldBe("cheapest");
        doc.Weights.Success.ShouldBe(3);
        doc.Rules[0].Strategy.ShouldBe("best_success");
        doc.Rules[0].Route.ShouldBeEmpty(); // yalnız strateji veren kural geçerli
    }
}
