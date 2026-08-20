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

        var eligible = FilterEligible(active, document.Guards);
        var skippedNote = active.Count != eligible.Count
            ? $" (sağlıksız {active.Count - eligible.Count} hesap atlandı)"
            : "";

        if (eligible.Count == 0)
            return new RoutingDecision([], "Tüm hesaplar sağlıksız — yönlendirilecek yol yok." + skippedNote,
                0, rule?.Name, rule?.Version);

        var candidates = await BuildCandidatesAsync(eligible, facts, ct);
        var decision = DecideCore(document, facts, candidates, rule?.Name, rule?.Version);
        return skippedNote.Length == 0 ? decision : decision with { Reason = decision.Reason + skippedNote };
    }

    /// <summary>Down hesaplar her zaman, skipUnhealthy açıkken Degraded hesaplar da elenir.</summary>
    public static List<ConnectorAccountSnapshot> FilterEligible(
        IReadOnlyList<ConnectorAccountSnapshot> active, RuleGuards guards)
        => active
            .Where(a => a.Health != ConnectorHealth.Down)
            .Where(a => !guards.SkipUnhealthy || a.Health == ConnectorHealth.Healthy)
            .ToList();

    /// <summary>
    /// Karar çekirdeği (saf, DB'siz): kural → hacim bölüşümü → strateji, ardından failover
    /// zinciri. Motor ve simülatör AYNI yoldan geçer — karar mantığı yalnız burada yaşar,
    /// yoksa simülasyon gerçek davranıştan sapar.
    /// </summary>
    public static RoutingDecision DecideCore(
        RuleDocument document, RoutingFacts facts, IReadOnlyList<RoutingCandidate> candidates,
        string? ruleName = null, int? ruleVersion = null)
    {
        var byReference = BuildReferenceMap(candidates);

        List<RoutingCandidate> primary;
        string reason;
        var strategy = Normalize(document.Strategy);

        // Sırayı strateji mi kurdu? Kural sabit rota verdiyse (elle sabitlenmiş yol) ya da
        // hacim bölüşümü seçtiyse ölçüm kotası devreye GİRMEZ: ikisi de işyerinin açık
        // talimatıdır, kota onları ezmemelidir.
        var strategyOrdered = false;

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
            {
                primary = [.. RoutingStrategies.Order(primary, strategy, document.Weights)];
                strategyOrdered = true;
            }

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
            strategyOrdered = true;
            reason = strategy == RoutingStrategies.Priority
                ? "Öncelik sırası (aktif kural eşleşmedi)."
                : $"Strateji: {Describe(strategy)} — {ExplainWinner(primary.FirstOrDefault(), strategy)}";
        }

        // Ölçüm kotası: kazanan tüm trafiği alırsa kaybeden örnek toplayamaz ve sinyali
        // ölünce kalıcı olarak sona düşer. Kotaya düşen istek, ölçülmemiş bir adayı başa
        // alır; kazanan hemen ARKASINDA kalır — deneme başarısız olursa failover onu yakalar,
        // yani ölçüm bedava değil ama tahsilatı riske atmaz.
        if (strategyOrdered && Explore(primary, strategy, document.Guards, facts.Seed)
            is (var explored, var bucket, var quota))
        {
            primary = [explored, .. primary.Where(c => c.AccountId != explored.AccountId)];
            reason = $"Ölçüm kotası: sinyali olmayan {explored.Label} öne alındı "
                     + $"(kova %{bucket} < %{quota}; sıra stratejisi: {Describe(strategy)}).";
        }

        // Failover zinciri: birincil + fallback + kalan uygun hesaplar (tekilleştirilmiş)
        var chain = primary.Select(c => c.AccountId)
            .Concat(Resolve(document.Fallback, byReference))
            .Concat(candidates.Select(c => c.AccountId))
            .Distinct()
            .ToList();

        return new RoutingDecision(
            chain,
            reason,
            Math.Max(1, document.Guards.MaxAttempts),
            ruleName,
            ruleVersion,
            strategy,
            [.. chain.Select(id => candidates.First(c => c.AccountId == id))]);
    }

    /// <summary>Hesapları maliyet ve performans sinyalleriyle zenginleştirir.</summary>
    private async Task<List<RoutingCandidate>> BuildCandidatesAsync(
        List<ConnectorAccountSnapshot> eligible, RoutingFacts facts, CancellationToken ct)
    {
        // Kart bankası maliyet sorgusuna girer: on-us oranı ancak kart biliniyorsa uygulanır
        var rateList = await rates.GetRatesAsync(facts.Installments, facts.Card?.BankCode, ct);
        var rateByAccount = rateList.ToDictionary(r => r.ConnectorAccountId, r => r.RateBps);

        var performanceList = await performance.GetAsync(PerformanceWindow, ct);
        var performanceByAccount = performanceList.ToDictionary(p => p.ConnectorAccountId);

        return eligible
            .Select(account => Enrich(account, facts.AmountMinor, rateByAccount, performanceByAccount))
            .ToList();
    }

    /// <summary>
    /// Tek hesabı sinyalleriyle adaya çevirir — motor ve simülatör AYNI zenginleştirmeden
    /// geçer; yuvarlama, örnek eşiği veya yeni bir sinyal değişirse iki taraf birlikte değişir.
    /// </summary>
    public static RoutingCandidate Enrich(
        ConnectorAccountSnapshot account, long amountMinor,
        IReadOnlyDictionary<Guid, int> rateBpsByAccount,
        IReadOnlyDictionary<Guid, ConnectorPerformance> performanceByAccount)
    {
        long? expectedCost = rateBpsByAccount.TryGetValue(account.Id, out var bps)
            ? (long)Math.Round(amountMinor * (bps / 10_000m), 0, MidpointRounding.ToEven)
            : null;

        performanceByAccount.TryGetValue(account.Id, out var stats);
        var reliable = stats is { SampleSize: >= RoutingStrategies.MinimumSample };

        return new RoutingCandidate(
            account.Id,
            account.Label,
            expectedCost,
            reliable ? stats!.AuthRate : null,
            reliable && stats!.MedianLatencyMs > 0 ? stats.MedianLatencyMs : null);
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

    private static Dictionary<string, RoutingCandidate> BuildReferenceMap(
        IReadOnlyList<RoutingCandidate> candidates)
    {
        var map = new Dictionary<string, RoutingCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            map.TryAdd(candidate.Label, candidate);
            map.TryAdd(candidate.AccountId.ToString(), candidate);
        }

        return map;
    }

    private static List<Guid> Resolve(
        IEnumerable<string> references, Dictionary<string, RoutingCandidate> map)
        => references
            .Select(r => map.GetValueOrDefault(r))
            .Where(c => c is not null)
            .Select(c => c!.AccountId)
            .ToList(); // bilinmeyen referanslar sessizce atlanır — hesap kapatılmış olabilir

    /// <summary>
    /// Ölçüm kotası. Sıranın BAŞINDAKİ aday zaten denenecek — kota yalnız arkada kalmış,
    /// sinyalsiz adaylar için anlamlıdır. Kova hacim bölüşümünden AYRI tuzlanır: aynı
    /// tohumda iki karar birbirine kilitlenmesin (aksi hâlde "%10 kotaya düşen istek"
    /// ile "%10'luk bölüşüm kovası" hep aynı işlemler olurdu).
    /// </summary>
    private static (RoutingCandidate Candidate, int Bucket, int Quota)? Explore(
        IReadOnlyList<RoutingCandidate> ordered, string strategy, RuleGuards guards, Guid seed)
    {
        if (!RoutingStrategies.UsesMeasuredSignals(strategy))
            return null;

        // Deneme hakkı tekse ölçümün arkasında failover YOKTUR: sinyalsiz bir POS'a
        // yönlendirmek tahsilatı doğrudan kumara çevirirdi. Ölçüm ancak kurtarma varken
        // göze alınabilir — kota sessizce kapanır.
        if (guards.MaxAttempts < 2)
            return null;

        var quota = Math.Clamp(guards.ExplorePercent, 0, 50);
        if (quota == 0 || ordered.Count < 2)
            return null;

        var pool = ordered.Skip(1).Where(c => RoutingStrategies.IsUnmeasured(c, strategy)).ToList();
        if (pool.Count == 0)
            return null; // ölçülmeye muhtaç aday yok — kota harcanmaz

        var bucket = Bucket(seed, "explore:");
        // Havuzda birden çok sinyalsiz aday varsa kova onları da deterministik olarak paylaştırır
        return bucket < quota ? (pool[bucket % pool.Count], bucket, quota) : null;
    }

    /// <summary>
    /// Tohumdan 0..99 deterministik kova — aynı intent her zaman aynı sonuca düşer
    /// (tekrar oynatılabilir karar; simülatör ile motor aynı kovayı görür).
    /// Tuz, aynı tohum üzerinden verilen farklı kararları birbirinden bağımsız kılar;
    /// hacim bölüşümü tuzsuzdur — sözleşmesi altın değerlerle pinlenmiştir.
    /// </summary>
    private static int Bucket(Guid seed, string salt = "")
        => (int)(BitConverter.ToUInt32(
            SHA256.HashData(Encoding.UTF8.GetBytes(salt + seed.ToString("N"))), 0) % 100);

    private static (Guid Id, string Label, int Bucket)? PickBySplit(
        List<VolumeSplitEntry> split, Dictionary<string, RoutingCandidate> map, Guid seed)
    {
        var bucket = Bucket(seed);

        var cumulative = 0;
        foreach (var entry in split)
        {
            cumulative += entry.Percent;
            if (bucket < cumulative && map.TryGetValue(entry.Account, out var candidate))
                return (candidate.AccountId, candidate.Label, bucket);
        }

        return null;
    }
}
