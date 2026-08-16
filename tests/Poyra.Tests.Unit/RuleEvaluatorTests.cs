using System.Text.Json;
using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Routing.Dsl;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class RuleEvaluatorTests
{
    private static RoutingFacts Facts(long amount = 50_000, string currency = "TRY",
        int installments = 1, int hour = 14)
        => new(Guid.NewGuid(), amount, currency, installments, hour);

    private static RuleDocument Doc(string json) => RuleDocument.Parse(json);

    [Fact]
    public void Amount_gte_kurali_eslesmelidir()
    {
        var doc = Doc("""
            { "rules": [ { "name": "yuksek",
                           "when": { "fact": "amount_minor", "op": "gte", "value": 100000 },
                           "route": ["A"] } ] }
            """);

        RuleEvaluator.FirstMatch(doc, Facts(amount: 150_000))!.Name.ShouldBe("yuksek");
        RuleEvaluator.FirstMatch(doc, Facts(amount: 50_000)).ShouldBeNull();
    }

    [Fact]
    public void All_ve_any_bilesikleri_dogru_calismali()
    {
        var doc = Doc("""
            { "rules": [ { "name": "gece-buyuk-taksit",
                "when": { "all": [
                    { "fact": "installments", "op": "gt", "value": 1 },
                    { "any": [
                        { "fact": "hour", "op": "gte", "value": 22 },
                        { "fact": "hour", "op": "lt", "value": 6 } ] } ] },
                "route": ["B"] } ] }
            """);

        RuleEvaluator.FirstMatch(doc, Facts(installments: 6, hour: 23)).ShouldNotBeNull();
        RuleEvaluator.FirstMatch(doc, Facts(installments: 6, hour: 3)).ShouldNotBeNull();
        RuleEvaluator.FirstMatch(doc, Facts(installments: 6, hour: 14)).ShouldBeNull();
        RuleEvaluator.FirstMatch(doc, Facts(installments: 1, hour: 23)).ShouldBeNull();
    }

    [Fact]
    public void Currency_eq_ve_in_operatorleri_calismali()
    {
        var doc = Doc("""
            { "rules": [
                { "name": "doviz", "when": { "fact": "currency", "op": "in", "value": ["USD","EUR"] }, "route": ["D"] },
                { "name": "tl", "when": { "fact": "currency", "op": "eq", "value": "try" }, "route": ["T"] } ] }
            """);

        RuleEvaluator.FirstMatch(doc, Facts(currency: "EUR"))!.Name.ShouldBe("doviz");
        RuleEvaluator.FirstMatch(doc, Facts(currency: "TRY"))!.Name.ShouldBe("tl"); // büyük/küçük harf duyarsız
    }

    [Fact]
    public void When_null_kural_her_zaman_eslesmelidir()
    {
        var doc = Doc("""{ "rules": [ { "name": "varsayilan", "route": ["X"] } ] }""");
        RuleEvaluator.FirstMatch(doc, Facts())!.Name.ShouldBe("varsayilan");
    }

    [Fact]
    public void Bilinmeyen_fact_eslesmemeli()
    {
        var doc = Doc("""
            { "rules": [ { "when": { "fact": "bin_bank", "op": "eq", "value": "isbank" }, "route": ["A"] } ] }
            """);
        RuleEvaluator.FirstMatch(doc, Facts()).ShouldBeNull(); // sessiz genişletilebilirlik
    }

    [Fact]
    public void Ilk_eslesen_kural_kazanmali()
    {
        var doc = Doc("""
            { "rules": [
                { "name": "birinci", "when": { "fact": "amount_minor", "op": "gt", "value": 10 }, "route": ["A"] },
                { "name": "ikinci", "when": { "fact": "amount_minor", "op": "gt", "value": 5 }, "route": ["B"] } ] }
            """);
        RuleEvaluator.FirstMatch(doc, Facts(amount: 100))!.Name.ShouldBe("birinci");
    }

    [Fact]
    public void Gecersiz_dokuman_parse_hatasi_vermeli()
        => Should.Throw<JsonException>(() => RuleDocument.Parse("bozuk json"));
}
