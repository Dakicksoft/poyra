using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Risk.Domain;
using Poyra.SharedKernel.Rules;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Risk.Infrastructure;

/// <summary>
/// Risk kapısının uygulaması. Payments bu tipi TANIMAZ — portu kendi Contracts'ında
/// tanımlar, biz uygularız.
///
/// Karar sırası bilinçlidir:
///  1- Kara liste — açık bir insan kararıdır, hiçbir kural onu ezmez
///  2- Kural seti — eşleşen İLK kural kazanır (rota motoruyla aynı semantik)
///  3- Varsayılan (allow)
///
/// Her karar kaydedilir; 'allow' bile — çünkü "o gün hangi kural bakmıştı" sorusu
/// engellenmeyen ama sonradan chargeback'e dönen işlemde de sorulur.
/// </summary>
public sealed class RiskEngine(
    RiskDbContext db,
    TenantContext tenant,
    IPaymentVelocitySource velocity,
    IClock clock) : IRiskGate
{
    public async Task<RiskDecision> AssessAsync(RiskContext context, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var counters = await velocity.GetAsync(
            context.CustomerRef, context.IpAddress, context.MaskedPan, now, ct);

        var blocklistHit = await FindBlocklistHitAsync(context, now, ct);
        var facts = BuildFacts(context, counters, blocklistHit, TurkeyHour(now));

        var decision = blocklistHit is not null
            ? new RiskDecision(RiskOutcomes.Block, "blocklist",
                $"Kara listede: {BlocklistKindMap.ToDb[blocklistHit.Kind]} — {blocklistHit.Reason}",
                facts.Snapshot())
            : await EvaluateRulesAsync(facts, ct);

        await RecordAsync(context, decision, facts, ct);
        return decision;
    }

    // ---- 1- Kara liste ------------------------------------------------------------

    private async Task<BlocklistEntry?> FindBlocklistHitAsync(
        RiskContext context, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = new List<(BlocklistKind Kind, string Value)>();

        void Add(BlocklistKind kind, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                candidates.Add((kind, BlocklistEntry.Normalize(value)));
        }

        Add(BlocklistKind.Card, context.MaskedPan);
        Add(BlocklistKind.Ip, context.IpAddress);
        Add(BlocklistKind.CustomerRef, context.CustomerRef);
        Add(BlocklistKind.Bin, context.Bin);
        Add(BlocklistKind.Country, context.Country);

        if (candidates.Count == 0)
            return null;

        var kinds = candidates.Select(c => c.Kind).Distinct().ToList();
        var values = candidates.Select(c => c.Value).Distinct().ToList();

        // Tek sorgu, sonra bellekte tam eşleşme: (kind, value) çiftini SQL'de
        // OR zinciriyle kurmak sorgu planını her çağrıda değiştirirdi
        var rows = await db.BlocklistEntries.AsNoTracking()
            .Where(e => e.RemovedAt == null
                        && (e.ExpiresAt == null || e.ExpiresAt > now)
                        && kinds.Contains(e.Kind)
                        && values.Contains(e.Value))
            .ToListAsync(ct);

        return rows.FirstOrDefault(r => candidates.Any(c => c.Kind == r.Kind && c.Value == r.Value));
    }

    // ---- 2- Kural seti ------------------------------------------------------------

    private async Task<RiskDecision> EvaluateRulesAsync(RuleFacts facts, CancellationToken ct)
    {
        var active = await db.RiskRuleSets.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Active, ct);

        if (active is null)
            return RiskDecision.Allowed;

        RiskDocument document;
        try
        {
            document = RiskDocument.Parse(active.Document);
        }
        catch (JsonException)
        {
            // Bozuk doküman tahsilatı DURDURMAZ: yayınlama sırasında doğrulanır,
            // buraya düşmesi bir hata durumudur ve izin vermek doğru taraftır.
            return RiskDecision.Allowed;
        }

        var match = document.Rules.FirstOrDefault(r => r.When is null || RuleEngine.Evaluate(r.When, facts));
        if (match is null)
            return new RiskDecision(
                RiskOutcomes.All.Contains(document.Default) ? document.Default : RiskOutcomes.Allow,
                Signals: facts.Snapshot());

        var outcome = RiskOutcomes.All.Contains(match.Outcome) ? match.Outcome : RiskOutcomes.Review;
        return new RiskDecision(outcome, match.Name, match.Reason, facts.Snapshot());
    }

    // ---- Sinyaller ---------------------------------------------------------------

    /// <summary>
    /// Kural dilinin gördüğü sinyaller. Rota motorunun sinyalleri AYNI adlarla
    /// buradadır (amount_minor, hour, card.*) — işyeri iki ayrı sözlük öğrenmez.
    ///
    /// public: kural deneme ucu (/v1/risk/test) ve testler aynı sinyal üretimini
    /// kullanmalı — ayrı bir kopya, denenen kuralla koşan kuralı ayırırdı.
    /// </summary>
    public static RuleFacts BuildFacts(
        RiskContext context, VelocitySnapshot counters, BlocklistEntry? blocklistHit, int turkeyHour)
        => new RuleFacts()
            .Set("amount_minor", context.AmountMinor)
            .Set("installments", (long)context.Installments)
            .Set("hour", (long)turkeyHour)
            .Set("currency", context.Currency)
            .Set("flow", context.Flow)
            .Set("customer_ref", context.CustomerRef)
            .Set("ip", context.IpAddress)
            .Set("country", context.Country)
            .Set("bin", context.Bin)
            .Set("card.bank", context.BankCode)
            .Set("card.program", context.Program)
            .Set("card.brand", context.Brand)
            .Set("card.type", context.CardType)
            .Set("card.commercial", context.IsCommercial)
            .Set("velocity.attempts_1h", (long)counters.Attempts1h)
            .Set("velocity.attempts_24h", (long)counters.Attempts24h)
            .Set("velocity.declines_1h", (long)counters.Declines1h)
            .Set("velocity.amount_24h", counters.Amount24hMinor)
            .Set("velocity.distinct_cards_24h", (long)counters.DistinctCards24h)
            .Set("blocklist.hit", blocklistHit is not null)
            .Set("blocklist.kind", blocklistHit is null ? null : BlocklistKindMap.ToDb[blocklistHit.Kind])
            .Alias("bank_code", "card.bank")
            .Alias("program", "card.program")
            .Alias("brand", "card.brand")
            .Alias("card_type", "card.type");

    /// <summary>
    /// Türkiye sabit UTC+3'tür (2016'dan beri DST yok) — "gece kuralı" TR saatine
    /// göre yazılır. Saat IClock'tan gelir: DateTimeOffset.UtcNow'a doğrudan bakmak
    /// kuralı testte sabitlenemez hale getirirdi.
    /// </summary>
    public static int TurkeyHour(DateTimeOffset now) => now.ToOffset(TimeSpan.FromHours(3)).Hour;

    private async Task RecordAsync(
        RiskContext context, RiskDecision decision, RuleFacts facts, CancellationToken ct)
    {
        var version = decision.RuleName is null or "blocklist"
            ? null
            : await db.RiskRuleSets.AsNoTracking()
                .Where(r => r.Active).Select(r => (int?)r.Version).SingleOrDefaultAsync(ct);

        db.RiskAssessments.Add(new RiskAssessment
        {
            TenantId = tenant.TenantId,
            PaymentId = context.PaymentId,
            Outcome = decision.Outcome,
            RuleName = decision.RuleName,
            Reason = decision.Reason,
            RuleVersion = version,
            Flow = context.Flow,
            Signals = JsonSerializer.Serialize(facts.Snapshot()),
        });

        await db.SaveChangesAsync(ct);
    }
}
