using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Routing.Dsl;

namespace Poyra.Modules.Routing.Infrastructure;

public static class RoutingStrategies
{
    public const string Priority = "priority";
    public const string Cheapest = "cheapest";
    public const string BestSuccess = "best_success";
    public const string Fastest = "fastest";
    public const string Balanced = "balanced";

    /// <summary>Performans sinyali bu örnek sayısının altındaysa güvenilmez sayılır.</summary>
    public const int MinimumSample = 20;

    public static bool IsKnown(string strategy)
        => strategy is Priority or Cheapest or BestSuccess or Fastest or Balanced;

    /// <summary>
    /// Strateji ÖLÇÜLEN sinyale mi dayanıyor? Ölçülen sinyaller (başarı oranı, gecikme)
    /// yalnız trafik akarken tazelenir — bu stratejiler ölçüm kotasına muhtaçtır.
    /// cheapest yapılandırılmış anlaşma oranını okur (trafikten bağımsız), priority ise
    /// hesabın önceliğini; ikisi de kotaya girmez.
    /// </summary>
    public static bool UsesMeasuredSignals(string strategy)
        => strategy is BestSuccess or Fastest or Balanced;

    /// <summary>
    /// Adayın, ilgili stratejinin baktığı sinyali YOK mu? İki hâlde olur: hesap yeni
    /// (hiç ölçülmedi) ya da eski kazanan kaybettiği için penceresi boşaldı. Her ikisi de
    /// "ölçülmeye muhtaç" demektir; kota tam bu adaylara ayrılır.
    /// </summary>
    public static bool IsUnmeasured(RoutingCandidate candidate, string strategy) => strategy switch
    {
        BestSuccess => candidate.AuthRate is null,
        Fastest => candidate.MedianLatencyMs is null,
        Balanced => candidate.AuthRate is null || candidate.MedianLatencyMs is null,
        _ => false,
    };

    /// <summary>
    /// Adayları stratejiye göre sıralar. Sinyali OLMAYAN aday elenmez — sona alınır:
    /// yeni eklenen bir POS "veri yok" diye tamamen dışlanmaz, ama öne de geçmez.
    /// Eşitlikte öncelik sırası (giriş sırası) korunur — karar deterministiktir.
    /// </summary>
    public static IReadOnlyList<RoutingCandidate> Order(
        IReadOnlyList<RoutingCandidate> candidates, string strategy, StrategyWeights weights)
        => strategy switch
        {
            Cheapest => [.. candidates.OrderBy(c => c.ExpectedCostMinor ?? long.MaxValue)],
            BestSuccess => [.. candidates.OrderByDescending(c => c.AuthRate ?? -1)],
            Fastest => [.. candidates.OrderBy(c => c.MedianLatencyMs ?? int.MaxValue)],
            Balanced => [.. candidates.OrderByDescending(c => Score(c, candidates, weights))],
            _ => candidates, // priority: gelen sıra (hesap önceliği) korunur
        };

    /// <summary>
    /// Dengeli skor: maliyet ve gecikme küçükken, başarı oranı büyükken iyidir.
    /// Her sinyal aday kümesi içinde 0..1'e normalize edilir — birimleri (kuruş/ms/oran)
    /// karşılaştırılabilir hale getirir. Sinyali olmayan aday o boyutta nötr (0.5) sayılır.
    /// </summary>
    public static double Score(
        RoutingCandidate candidate, IReadOnlyList<RoutingCandidate> all, StrategyWeights weights)
    {
        var costScore = InvertedNormalized(
            candidate.ExpectedCostMinor, all.Select(c => (double?)c.ExpectedCostMinor));
        var latencyScore = InvertedNormalized(
            candidate.MedianLatencyMs, all.Select(c => (double?)c.MedianLatencyMs));
        var successScore = candidate.AuthRate ?? 0.5;

        var total = weights.Cost + weights.Success + weights.Latency;
        if (total <= 0)
            return 0;

        return (costScore * weights.Cost + successScore * weights.Success + latencyScore * weights.Latency) / total;
    }

    /// <summary>Küçük değerin iyi olduğu boyutlar için 0..1 normalizasyon (min=1, max=0).</summary>
    private static double InvertedNormalized(double? value, IEnumerable<double?> population)
    {
        if (value is null)
            return 0.5; // sinyal yok → nötr

        var values = population.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (values.Count == 0)
            return 0.5;

        var min = values.Min();
        var max = values.Max();
        return max - min < double.Epsilon ? 1 : 1 - (value.Value - min) / (max - min);
    }
}
