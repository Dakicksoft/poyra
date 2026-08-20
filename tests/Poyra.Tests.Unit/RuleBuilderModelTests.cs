using System.Text.Json;
using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Routing.Dsl;
using Poyra.Panel.Routing;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Görsel kural kurucunun ürettiği JSON, motorun ANLADIĞI JSON olmalıdır. Buradaki testler
/// kurucuyu gerçek <see cref="RuleEvaluator"/> üzerinde koşturur — arayüz ile motorun
/// birbirinden ayrı düşmesi yanlış POS'a yönlendirme demektir.
/// </summary>
public sealed class RuleBuilderModelTests
{
    private static RoutingFacts Facts(
        long amountMinor = 10_000, int installments = 1, string? program = null,
        string? bankCode = null, bool commercial = false)
        => new(Guid.NewGuid(), amountMinor, "TRY", installments, 14,
            new CardFacts("540061", bankCode, program, "mastercard", "credit", commercial));

    private static RuleDocument Build(RuleBuilderModel model)
        => RuleDocument.Parse(model.ToJson().GetRawText());

    [Fact]
    public void Tutar_kosulu_lirayi_kurusa_cevirmeli()
    {
        var model = new RuleBuilderModel();
        model.Rules.Add(new RuleBlock
        {
            Name = "yüksek tutar",
            Conditions = [new ConditionRow { Fact = "amount_minor", Op = "gte", Value = "1.500" }],
            Route = ["İş POS"],
        });

        // Kullanıcı ₺ yazar, DSL kuruş taşır — kurucu tek dönüşüm noktasıdır
        var json = model.ToJson().GetRawText();
        json.ShouldContain("150000");

        var document = Build(model);
        RuleEvaluator.FirstMatch(document, Facts(amountMinor: 149_999)).ShouldBeNull();
        RuleEvaluator.FirstMatch(document, Facts(amountMinor: 150_000))!.Route.ShouldBe(["İş POS"]);
    }

    [Fact]
    public void Coklu_kosul_ve_veya_olarak_baglanmali()
    {
        var model = new RuleBuilderModel();
        model.Rules.Add(new RuleBlock
        {
            Name = "bonus taksitli",
            Join = "all",
            Conditions =
            [
                new ConditionRow { Fact = "card.program", Op = "eq", Value = "bonus" },
                new ConditionRow { Fact = "installments", Op = "gte", Value = "3" },
            ],
            Route = ["Garanti POS"],
        });

        var document = Build(model);
        RuleEvaluator.FirstMatch(document, Facts(program: "bonus", installments: 3)).ShouldNotBeNull();
        RuleEvaluator.FirstMatch(document, Facts(program: "bonus", installments: 2)).ShouldBeNull();
        RuleEvaluator.FirstMatch(document, Facts(program: "world", installments: 6)).ShouldBeNull();

        // Aynı koşullar "veya" ile gevşer
        model.Rules[0].Join = "any";
        var loose = Build(model);
        RuleEvaluator.FirstMatch(loose, Facts(program: "world", installments: 6)).ShouldNotBeNull();
    }

    [Fact]
    public void Tek_kosul_all_ile_sarilmamali()
    {
        var model = new RuleBuilderModel();
        model.Rules.Add(new RuleBlock
        {
            Conditions = [new ConditionRow { Fact = "card.brand", Op = "eq", Value = "troy" }],
        });

        // Üretilen JSON elle de okunabilir kalmalı: gereksiz sarmalayıcı yok
        model.ToJson().GetRawText().ShouldNotContain("\"all\"");
    }

    [Fact]
    public void In_operatoru_listeyi_diziye_cevirmeli()
    {
        var model = new RuleBuilderModel();
        model.Rules.Add(new RuleBlock
        {
            Conditions = [new ConditionRow { Fact = "card.program", Op = "in", Value = "bonus, world , axess" }],
            Route = ["Ana POS"],
        });

        var document = Build(model);
        RuleEvaluator.FirstMatch(document, Facts(program: "world")).ShouldNotBeNull();
        RuleEvaluator.FirstMatch(document, Facts(program: "maximum")).ShouldBeNull();
    }

    /// <summary>
    /// Görsel kurucunun fact kataloğu ile motorun tanıdığı fact'lerin BİREBİR aynı olması
    /// bugüne dek yalnız bir yorumla korunuyordu. Katalogda olup motorda olmayan bir fact,
    /// panelde seçilebilir ama hiçbir işlemde eşleşmeyen — yani sessizce ölü — kural üretir.
    /// Test her katalog girdisini motorda sınar: bilinen bir fact'e "asla eşit olmayan değer"
    /// sorulduğunda true döner; motor o fact'i hiç tanımıyorsa torbada yok sayılır ve false döner.
    /// </summary>
    [Theory]
    [MemberData(nameof(KatalogFactleri))]
    public void Katalogdaki_her_fact_motorda_tanimli_olmali(string fact, FactKind kind)
    {
        var absurd = kind switch
        {
            FactKind.Money or FactKind.Number => "-987654321",
            FactKind.Boolean => "false", // dolu fact'te true kuruldu → ne:false eşleşir
            _ => "\"___boyle_bir_deger_yok___\"",
        };

        var document = RuleDocument.Parse($$"""
            { "rules": [ { "when": { "fact": "{{fact}}", "op": "ne", "value": {{absurd}} },
                           "route": ["A"] } ] }
            """);

        RuleEvaluator.FirstMatch(document, TumSinyallerDolu())
            .ShouldNotBeNull($"'{fact}' katalogda var ama motorun fact torbasında yok — " +
                             "RuleEvaluator.ToFacts'e eklenmemiş.");
    }

    public static TheoryData<string, FactKind> KatalogFactleri()
    {
        var data = new TheoryData<string, FactKind>();
        foreach (var definition in RuleFacts.All)
            data.Add(definition.Fact, definition.Kind);

        return data;
    }

    /// <summary>Her sinyali BİLİNEN bir işlem — katalog taramasının zemini.</summary>
    private static RoutingFacts TumSinyallerDolu()
        => new(Guid.NewGuid(), 150_000, "TRY", 6, 14,
            new CardFacts("540061", "0062", "bonus", "mastercard", "credit", IsCommercial: true,
                Country: "TR"),
            PaymentChannels.Api);

    [Fact]
    public void Bilesik_alanlar_json_ile_gidip_gelmeli()
    {
        var model = new RuleBuilderModel
        {
            Strategy = "balanced",
            MaxAttempts = 3,
            SkipUnhealthy = false,
            ExplorePercent = 25,
            CostWeight = 2,
            LatencyWeight = 0.25,
            Fallback = ["Yedek POS"],
            Split = [new SplitRow { Account = "A POS", Percent = 70 }, new SplitRow { Account = "B POS", Percent = 30 }],
        };
        model.Rules.Add(new RuleBlock
        {
            Name = "ticari kart",
            Conditions = [new ConditionRow { Fact = "card.commercial", Op = "eq", Value = "true" }],
            Route = ["Kurumsal POS"],
            Strategy = "cheapest",
            Reason = "ticari kartlar kurumsal POS'a",
        });

        var round = RuleBuilderModel.FromJson(model.ToJson());

        round.Advanced.ShouldBeFalse();
        round.Strategy.ShouldBe("balanced");
        round.MaxAttempts.ShouldBe(3);
        round.SkipUnhealthy.ShouldBeFalse();
        round.ExplorePercent.ShouldBe(25); // görsel kurucuda düzenlenen kural kotayı düşürmemeli
        round.CostWeight.ShouldBe(2);
        round.LatencyWeight.ShouldBe(0.25);
        round.Fallback.ShouldBe(["Yedek POS"]);
        round.Split.Select(s => (s.Account, s.Percent)).ShouldBe([("A POS", 70), ("B POS", 30)]);

        var rule = round.Rules.ShouldHaveSingleItem();
        rule.Name.ShouldBe("ticari kart");
        rule.Route.ShouldBe(["Kurumsal POS"]);
        rule.Strategy.ShouldBe("cheapest");
        rule.Reason.ShouldBe("ticari kartlar kurumsal POS'a");
        rule.Conditions.ShouldHaveSingleItem().Value.ShouldBe("true");
    }

    [Fact]
    public void Tutar_geri_okumada_liraya_donmeli()
    {
        var json = JsonDocument.Parse("""
            { "rules": [ { "when": { "fact": "amount_minor", "op": "gte", "value": 150000 } } ] }
            """).RootElement;

        var model = RuleBuilderModel.FromJson(json);

        model.Advanced.ShouldBeFalse();
        model.Rules[0].Conditions[0].Value.ShouldBe("1500"); // kullanıcı ₺ görür
    }

    [Fact]
    public void Fact_takma_adlari_taninmali()
    {
        // DSL 'program'/'bank_code' yazımlarını da kabul eder; kurucu bunları kanonik ada çeker
        var json = JsonDocument.Parse("""
            { "rules": [ { "when": { "all": [
                { "fact": "program", "op": "eq", "value": "bonus" },
                { "fact": "bank_code", "op": "eq", "value": "0062" } ] } } ] }
            """).RootElement;

        var model = RuleBuilderModel.FromJson(json);

        model.Advanced.ShouldBeFalse();
        model.Rules[0].Conditions.Select(c => c.Fact).ShouldBe(["card.program", "card.bank"]);
    }

    [Fact]
    public void Ic_ice_kosul_gelismis_sayilmali_ve_sadelestirilmemeli()
    {
        // Kurucu bunu çizemez. Sessizce düzleştirmek kuralın ANLAMINI değiştirirdi.
        var json = JsonDocument.Parse("""
            { "rules": [ { "when": { "all": [
                { "any": [ { "fact": "card.program", "op": "eq", "value": "bonus" },
                           { "fact": "card.program", "op": "eq", "value": "world" } ] },
                { "fact": "installments", "op": "gte", "value": 3 } ] } } ] }
            """).RootElement;

        RuleBuilderModel.FromJson(json).Advanced.ShouldBeTrue();
    }

    [Fact]
    public void Bilinmeyen_fact_gelismis_sayilmali()
    {
        var json = JsonDocument.Parse("""
            { "rules": [ { "when": { "fact": "musteri_segmenti", "op": "eq", "value": "vip" } } ] }
            """).RootElement;

        RuleBuilderModel.FromJson(json).Advanced.ShouldBeTrue();
    }

    [Fact]
    public void Bos_degerli_kosul_kurala_yazilmamali()
    {
        // Kullanıcı koşul satırı ekleyip doldurmadıysa kural HERKESE uymamalı, koşul düşmeli
        var model = new RuleBuilderModel();
        model.Rules.Add(new RuleBlock
        {
            Conditions =
            [
                new ConditionRow { Fact = "card.program", Op = "eq", Value = "bonus" },
                new ConditionRow { Fact = "bin", Op = "eq", Value = "" },
            ],
            Route = ["Ana POS"],
        });

        var document = Build(model);
        document.Rules[0].When!.All.ShouldBeNull(); // tek geçerli koşul kaldı → sarmalayıcı yok
        RuleEvaluator.FirstMatch(document, Facts(program: "bonus")).ShouldNotBeNull();
    }
}
