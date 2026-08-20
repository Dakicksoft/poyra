using Poyra.Modules.Routing.Contracts;
using Poyra.SharedKernel.Rules;

namespace Poyra.Modules.Routing.Dsl;

/// <summary>
/// Rota kuralı değerlendirmesi. Karşılaştırma operatörleri çekirdekteki
/// <see cref="RuleEngine"/>'dedir (risk motoruyla paylaşılır); burada yalnız
/// rota sinyalleri torbaya çevrilir.
/// </summary>
public static class RuleEvaluator
{
    /// <summary>İlk eşleşen kuralı döner; When=null her zaman eşleşir.</summary>
    public static RouteRule? FirstMatch(RuleDocument document, RoutingFacts facts)
    {
        var bag = ToFacts(facts);
        return document.Rules.FirstOrDefault(r => r.When is null || RuleEngine.Evaluate(r.When, bag));
    }

    public static bool Evaluate(RuleCondition condition, RoutingFacts facts)
        => RuleEngine.Evaluate(condition, ToFacts(facts));

    /// <summary>
    /// Kart sinyalleri yalnız BİLİNİYORSA torbaya girer: hosted akışta müşteri henüz
    /// kart girmemişken kartla ilgili kurallar eşleşmemeli, kural atlanıp bir sonrakine
    /// (ya da stratejiye) geçilmelidir.
    /// Kanal da aynı kuraldadır: kanal alanı eklenmeden önceki kayıtlarda null gelir ve
    /// kanal kuralları o işlemlerde eşleşmez (RuleFacts.Set boş değeri torbaya koymaz).
    /// </summary>
    private static RuleFacts ToFacts(RoutingFacts facts)
    {
        var card = facts.Card;

        return new RuleFacts()
            .Set("amount_minor", facts.AmountMinor)
            .Set("installments", (long)facts.Installments)
            .Set("hour", (long)facts.HourLocal)
            .Set("currency", facts.Currency)
            .Set("channel", facts.Channel)
            .Set("bin", card?.Bin)
            .Set("card.bank", card?.BankCode)
            .Set("card.program", card?.Program)
            .Set("card.brand", card?.Brand)
            .Set("card.type", card?.CardType)
            .Set("card.commercial", card?.IsCommercial)
            .Alias("bank_code", "card.bank")
            .Alias("program", "card.program")
            .Alias("brand", "card.brand")
            .Alias("card_type", "card.type");
    }
}
