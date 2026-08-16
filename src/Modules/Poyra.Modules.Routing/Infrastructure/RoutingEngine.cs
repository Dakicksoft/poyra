using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Routing.Domain;
using Poyra.Modules.Routing.Dsl;

namespace Poyra.Modules.Routing.Infrastructure;

/// <summary>
/// Rota motoru v2. Karar sırası:
///  1- aktif + sağlıklı hesapları topla, maliyet (anlaşma oranı) ve performans
///    (başarı oranı, gecikme p50) sinyalleriyle zenginleştir,
///  2- aktif kuralın ilk eşleşen satırı — sabit rota ve/veya strateji verebilir,
///  3- kural yoksa hacim bölüşümü (deterministik kova) — A/B için,
///  4- o da yoksa doküman stratejisi (varsayılan: priority).
/// Ardından fallback ve kalan uygun hesaplar zincire eklenir (failover).
/// Her karar insan-okur gerekçe + kullanılan sinyalleri döner (açıklanabilirlik).
/// </summary>
public sealed class RoutingEngine(
    RoutingDbContext db,
    IConnectorAccountsDirectory accounts,
    ICommissionRateSource rates,
    IConnectorPerformanceSource performance) : IRoutingService
{
    /// <summary>Performans sinyalinin penceresi — TR'de POS davranışı gün içinde değişebilir.</summary>
    public static readonly TimeSpan PerformanceWindow = TimeSpan.FromDays(7);

    public async Task<RoutingDecision> DecideAsync(RoutingFacts facts, CancellationToken ct)
    {
        var active = await accounts.GetActiveAccountsAsync(ct);
        if (active.Count == 0)
            return new RoutingDecision([], "Aktif bağlantı hesabı yok.", 0, null, null);

        var rule = await db.RoutingRules.AsNoTracking().SingleOrDefaultAsync(r => r.IsActive, ct);
        var document = rule is null ? new RuleDocument() : RuleDocument.Parse(rule.Document);
        var guards = document.Guards;

        var eligible = active
            .Where(a => a.Health != ConnectorHealth.Down)
            .Where(a => !guards.SkipUnhealthy || a.Health == ConnectorHealth.Healthy)
            .ToList();

        var skippedNote = active.Count != eligible.Count
            ? $" (sağlıksız {active.Count - eligible.Count} hesap atlandı)"
            : "";

        if (eligible.Count == 0)
            return new RoutingDecision([], "Tüm hesaplar sağlıksız — yönlendirilecek yol yok." + skippedNote,
                0, rule?.Name, rule?.Version);

        var candidates = await BuildCandidatesAsync(eligible, facts, ct);
        var byReference = BuildReferenceMap(eligible);

        List<RoutingCandidate> primary;
        string reason;
        var strategy = Normalize(document.Strategy);

        var matched = RuleEvaluator.FirstMatch(document, facts);
        if (matched is not null)
        {
            strategy = Normalize(matched.Strategy) is var ruleStrategy && ruleStrategy != RoutingStrategies.Priority
                ? ruleStrategy
                : matched.Strategy is null ? strategy : RoutingStrategies.Priority;

            var routed = Resolve(matched.Route, byReference);
            primary = routed.Count > 0
                ? [.. candidates.Where(c => routed.Contains(c.AccountId)).OrderBy(c => routed.IndexOf(c.AccountId))]
                : [.. candidates];

            // Kural sabit rota vermişse sıra korunur; yalnız strateji verdiyse strateji sıralar
            if (routed.Count == 0 || matched.Strategy is not null)
                primary = [.. RoutingStrategies.Order(primary, strategy, document.Weights)];

            reason = $"Kural eşleşti: {matched.Reason ?? matched.Name ?? "adsız kural"}"
                     + (strategy == RoutingStrategies.Priority ? "" : $" — strateji: {Describe(strategy)}");
        }
        else if (document.VolumeSplit.Count > 0
                 && PickBySplit(document.VolumeSplit, byReference, facts.Seed) is { } splitPick)
        {
            primary = [.. candidates.Where(c => c.AccountId == splitPick.Id)];
            reason = $"Hacim bölüşümü: kova %{splitPick.Bucket} → {splitPick.Label}";
        }
        else
        {
            primary = [.. RoutingStrategies.Order(candidates, strategy, document.Weights)];
            reason = strategy == RoutingStrategies.Priority
                ? "Öncelik sırası (aktif kural eşleşmedi)."
                : $"Strateji: {Describe(strategy)} — {ExplainWinner(primary.FirstOrDefault(), strategy)}";
        }

        // Failover zinciri: birincil + fallback + kalan uygun hesaplar (tekilleştirilmiş)
        var chain = primary.Select(c => c.AccountId)
            .Concat(Resolve(document.Fallback, byReference))
            .Concat(candidates.Select(c => c.AccountId))
            .Distinct()
            .ToList();

        return new RoutingDecision(
            chain,
            reason + skippedNote,
            Math.Max(1, guards.MaxAttempts),
            rule?.Name,
            rule?.Version,
            strategy,
            [.. chain.Select(id => candidates.First(c => c.AccountId == id))]);
    }

    /// <summary>Hesapları maliyet ve performans sinyalleriyle zenginleştirir.</summary>
    private async Task<List<RoutingCandidate>> BuildCandidatesAsync(
        List<ConnectorAccountSnapshot> eligible, RoutingFacts facts, CancellationToken ct)
    {
        var rateList = await rates.GetRatesAsync(facts.Installments, ct);
        var rateByAccount = rateList.ToDictionary(r => r.ConnectorAccountId, r => r.RateBps);

        var performanceList = await performance.GetAsync(PerformanceWindow, ct);
        var performanceByAccount = performanceList.ToDictionary(p => p.ConnectorAccountId);

        return eligible.Select(account =>
        {
            long? expectedCost = rateByAccount.TryGetValue(account.Id, out var bps)
                ? (long)Math.Round(facts.AmountMinor * (bps / 10_000m), 0, MidpointRounding.ToEven)
                : null;

            performanceByAccount.TryGetValue(account.Id, out var stats);
            var reliable = stats is { SampleSize: >= RoutingStrategies.MinimumSample };

            return new RoutingCandidate(
                account.Id,
                account.Label,
                expectedCost,
                reliable ? stats!.AuthRate : null,
                reliable && stats!.MedianLatencyMs > 0 ? stats.MedianLatencyMs : null);
        }).ToList();
    }

    private static string Normalize(string? strategy)
        => strategy is { Length: > 0 } value && RoutingStrategies.IsKnown(value)
            ? value
            : RoutingStrategies.Priority;

    private static string Describe(string strategy) => strategy switch
    {
        RoutingStrategies.Cheapest => "en düşük komisyon",
        RoutingStrategies.BestSuccess => "en yüksek başarı oranı",
        RoutingStrategies.Fastest => "en hızlı yanıt",
        RoutingStrategies.Balanced => "dengeli (maliyet + başarı + hız)",
        _ => "öncelik sırası",
    };

    /// <summary>Kazanan adayın SEÇİLME NEDENİ — panelde "neden bu POS" satırının gövdesi.</summary>
    private static string ExplainWinner(RoutingCandidate? winner, string strategy)
    {
        if (winner is null)
            return "aday yok";

        return strategy switch
        {
            RoutingStrategies.Cheapest => winner.ExpectedCostMinor is { } cost
                ? $"{winner.Label} beklenen komisyon {Kurus(cost)}"
                : $"{winner.Label} (komisyon anlaşması tanımsız)",
            RoutingStrategies.BestSuccess => winner.AuthRate is { } rate
                ? $"{winner.Label} başarı oranı %{rate * 100:0.0}"
                : $"{winner.Label} (yeterli örnek yok)",
            RoutingStrategies.Fastest => winner.MedianLatencyMs is { } ms
                ? $"{winner.Label} ortanca yanıt {ms} ms"
                : $"{winner.Label} (ölçüm yok)",
            RoutingStrategies.Balanced =>
                $"{winner.Label} — komisyon {(winner.ExpectedCostMinor is { } c ? Kurus(c) : "?")}, "
                + $"başarı {(winner.AuthRate is { } r ? $"%{r * 100:0.0}" : "?")}, "
                + $"yanıt {(winner.MedianLatencyMs is { } l ? $"{l} ms" : "?")}",
            _ => winner.Label,
        };
    }

    private static string Kurus(long amountMinor)
        => (amountMinor / 100m).ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " ₺";

    private static Dictionary<string, ConnectorAccountSnapshot> BuildReferenceMap(
        IReadOnlyList<ConnectorAccountSnapshot> accounts)
    {
        var map = new Dictionary<string, ConnectorAccountSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
        {
            map.TryAdd(account.Label, account);
            map.TryAdd(account.Id.ToString(), account);
        }

        return map;
    }

    private static List<Guid> Resolve(
        IEnumerable<string> references, Dictionary<string, ConnectorAccountSnapshot> map)
        => references
            .Select(r => map.GetValueOrDefault(r))
            .Where(a => a is not null)
            .Select(a => a!.Id)
            .ToList(); // bilinmeyen referanslar sessizce atlanır — hesap kapatılmış olabilir

    private static (Guid Id, string Label, int Bucket)? PickBySplit(
        List<VolumeSplitEntry> split, Dictionary<string, ConnectorAccountSnapshot> map, Guid seed)
    {
        // Deterministik kova: aynı intent her zaman aynı hesaba düşer (tekrar oynatılabilir karar)
        var bucket = (int)(BitConverter.ToUInt32(
            SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString("N"))), 0) % 100);

        var cumulative = 0;
        foreach (var entry in split)
        {
            cumulative += entry.Percent;
            if (bucket < cumulative && map.TryGetValue(entry.Account, out var account))
                return (account.Id, account.Label, bucket);
        }

        return null;
    }
}
