using System.Text.Json;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Risk.Domain;
using Poyra.Modules.Risk.Infrastructure;
using Poyra.SharedKernel.Rules;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Risk kural dili. Rota motoruyla AYNI çekirdek değerlendiriciyi kullanır; buradaki
/// testler sinyal adlarını ve risk-özel davranışı (bilinmeyen sinyal eşleşmez,
/// bilinmeyen sonuç 'review'a düşer) sabitler.
/// </summary>
public sealed class RiskFactsTests
{
    private static RuleFacts Facts(
        long amount = 10_000, string flow = "hosted", int hour = 14,
        string? bin = null, string? program = null,
        int attempts1h = 0, int declines1h = 0, int distinctCards = 0)
        => RiskEngine.BuildFacts(
            new RiskContext("pay_1", amount, "TRY", 1, flow, "cust-1", "10.0.0.1", null,
                bin, null, program, null, null, null, null),
            new VelocitySnapshot(attempts1h, attempts1h, declines1h, amount, distinctCards),
            blocklistHit: null,
            turkeyHour: hour);

    private static RuleCondition Condition(string json)
        => JsonSerializer.Deserialize<RuleCondition>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void Tutar_ve_saat_kurallari_eslesmeli()
    {
        var facts = Facts(amount: 750_000, hour: 3);

        RuleEngine.Evaluate(Condition("""
            {"all":[{"fact":"amount_minor","op":"gte","value":500000},
                    {"fact":"hour","op":"lte","value":5}]}
            """), facts).ShouldBeTrue();

        RuleEngine.Evaluate(Condition("""{"fact":"amount_minor","op":"lt","value":500000}"""), facts)
            .ShouldBeFalse();
    }

    [Fact]
    public void Hiz_sayaclari_kurala_girmeli()
    {
        var facts = Facts(attempts1h: 12, declines1h: 9, distinctCards: 7);

        // Kart deneme saldırısının imzası: çok deneme + çok ret + çok farklı kart
        RuleEngine.Evaluate(Condition("""
            {"all":[{"fact":"velocity.declines_1h","op":"gte","value":5},
                    {"fact":"velocity.distinct_cards_24h","op":"gte","value":3}]}
            """), facts).ShouldBeTrue();
    }

    [Fact]
    public void Akis_sinyali_direct_ile_hosted_ayirmali()
    {
        RuleEngine.Evaluate(Condition("""{"fact":"flow","op":"eq","value":"direct"}"""), Facts(flow: "direct"))
            .ShouldBeTrue();
        RuleEngine.Evaluate(Condition("""{"fact":"flow","op":"eq","value":"direct"}"""), Facts(flow: "hosted"))
            .ShouldBeFalse();
    }

    [Fact]
    public void Bilinmeyen_sinyal_ESLESMEMELI()
    {
        // BIN bilinmiyorsa (hosted akışta müşteri henüz kart girmedi) karta dayalı
        // kural sessizce tetiklenmemeli — aksi halde her ödeme engellenirdi
        var facts = Facts(bin: null);

        RuleEngine.Evaluate(Condition("""{"fact":"bin","op":"starts_with","value":"5"}"""), facts)
            .ShouldBeFalse();
        RuleEngine.Evaluate(Condition("""{"fact":"bin","op":"ne","value":"999999"}"""), facts)
            .ShouldBeFalse(); // 'ne' bile eşleşmez: yokluk "farklı" demek değildir
    }

    [Fact]
    public void Kart_sinyalleri_rota_motoruyla_ayni_adlarla_gelmeli()
    {
        var facts = Facts(bin: "540668", program: "bonus");

        RuleEngine.Evaluate(Condition("""{"fact":"card.program","op":"eq","value":"bonus"}"""), facts)
            .ShouldBeTrue();
        // Rota kuralında kullanılan takma ad da çalışmalı — işyeri iki sözlük öğrenmez
        RuleEngine.Evaluate(Condition("""{"fact":"program","op":"eq","value":"bonus"}"""), facts)
            .ShouldBeTrue();
    }

    [Fact]
    public void TR_saati_UTC_degil_UTC_arti_3_olmali()
    {
        // 23:30 UTC = ertesi gün 02:30 TR — "gece kuralı" TR saatine göre yazılır
        RiskEngine.TurkeyHour(new DateTimeOffset(2026, 8, 3, 23, 30, 0, TimeSpan.Zero)).ShouldBe(2);
        RiskEngine.TurkeyHour(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero)).ShouldBe(15);
    }

    [Fact]
    public void Kara_liste_isabeti_sinyal_olarak_gorunmeli()
    {
        var hit = new BlocklistEntry
        {
            TenantId = Guid.NewGuid(), Kind = BlocklistKind.Ip,
            Value = "10.0.0.1", Reason = "kart deneme",
        };

        var facts = RiskEngine.BuildFacts(
            new RiskContext("pay_1", 1000, "TRY", 1, "hosted", null, "10.0.0.1", null,
                null, null, null, null, null, null, null),
            new VelocitySnapshot(0, 0, 0, 0, 0), hit, 14);

        RuleEngine.Evaluate(Condition("""{"fact":"blocklist.hit","op":"eq","value":true}"""), facts)
            .ShouldBeTrue();
        RuleEngine.Evaluate(Condition("""{"fact":"blocklist.kind","op":"eq","value":"ip"}"""), facts)
            .ShouldBeTrue();
    }
}

public sealed class RiskDocumentTests
{
    [Fact]
    public void Ilk_eslesen_kural_kazanmali()
    {
        var document = RiskDocument.Parse("""
            {
              "rules": [
                { "name": "küçük tutar geç", "outcome": "allow",
                  "when": { "fact": "amount_minor", "op": "lt", "value": 5000 } },
                { "name": "her şeyi engelle", "outcome": "block" }
              ],
              "default": "review"
            }
            """);

        document.Rules.Count.ShouldBe(2);
        document.Default.ShouldBe("review");

        // When yoksa kural HER ZAMAN eşleşir — ikinci kural yakalayıcıdır
        document.Rules[1].When.ShouldBeNull();
    }

    [Fact]
    public void Varsayilan_sonuc_allow_olmali()
    {
        // "default" yazılmazsa risk motoru tahsilatı DURDURMAMALI: engelleme
        // açıkça yazılmış bir kuralın sonucu olmalıdır
        RiskDocument.Parse("""{"rules":[]}""").Default.ShouldBe("allow");
    }

    [Fact]
    public void Gecerli_sonuclar_sabit_olmali()
    {
        RiskOutcomes.All.ShouldBe(new HashSet<string> { "allow", "challenge", "review", "block" });
    }

    [Fact]
    public void Karar_yardimcilari_dogru_olmali()
    {
        new RiskDecision(RiskOutcomes.Block).Blocks.ShouldBeTrue();
        new RiskDecision(RiskOutcomes.Challenge).RequiresThreeDs.ShouldBeTrue();
        new RiskDecision(RiskOutcomes.Review).Blocks.ShouldBeFalse();
        new RiskDecision(RiskOutcomes.Review).RequiresThreeDs.ShouldBeFalse();
        RiskDecision.Allowed.Outcome.ShouldBe("allow");
    }
}

public sealed class BlocklistNormalizationTests
{
    [Fact]
    public void Deger_normallestirilmeli()
    {
        BlocklistEntry.Normalize("  Ahmet@Ornek.COM ").ShouldBe("ahmet@ornek.com");
        BlocklistEntry.Normalize("10.0.0.1").ShouldBe("10.0.0.1");
    }

    [Fact]
    public void Turkce_I_tuzagina_dusmemeli()
    {
        // .NET'te "İ".ToLowerInvariant() harfi DEĞİŞTİRMEZ. Katlama olmasaydı
        // kara listedeki "İstanbul@ornek.com", müşterinin "istanbul@ornek.com"
        // adresiyle eşleşmez ve engel sessizce çalışmazdı.
        "İ".ToLowerInvariant().ShouldBe("İ"); // .NET'in gerçek davranışı — testin dayanağı

        BlocklistEntry.Normalize("İSTANBUL@ornek.com").ShouldBe("istanbul@ornek.com");
        BlocklistEntry.Normalize("İstanbul@Ornek.com")
            .ShouldBe(BlocklistEntry.Normalize("istanbul@ornek.com"));
    }

    [Fact]
    public void Noktasiz_i_KATLANMAMALI()
    {
        // "ısparta@..." ile "isparta@..." FARKLI adreslerdir; birini engellemek
        // diğerini engellememelidir
        BlocklistEntry.Normalize("ısparta@ornek.com")
            .ShouldNotBe(BlocklistEntry.Normalize("isparta@ornek.com"));
    }

    [Fact]
    public void Suresi_gecmis_engel_aktif_olmamali()
    {
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

        Entry(expiresAt: now.AddHours(1)).IsActive(now).ShouldBeTrue();
        Entry(expiresAt: now.AddHours(-1)).IsActive(now).ShouldBeFalse();
        Entry(expiresAt: null).IsActive(now).ShouldBeTrue(); // süresiz

        var removed = Entry(expiresAt: null);
        removed.RemovedAt = now.AddMinutes(-5);
        removed.IsActive(now).ShouldBeFalse();

        static BlocklistEntry Entry(DateTimeOffset? expiresAt) => new()
        {
            TenantId = Guid.NewGuid(), Kind = BlocklistKind.Ip,
            Value = "10.0.0.1", Reason = "test", ExpiresAt = expiresAt,
        };
    }
}
