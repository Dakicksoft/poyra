using System.Diagnostics;

namespace Poyra.Tests.Load;

/// <param name="Name">Senaryo adı — raporda görünür.</param>
/// <param name="Concurrency">Eşzamanlı sanal kullanıcı (worker) sayısı.</param>
/// <param name="Duration">Yük süresi.</param>
/// <param name="Warmup">Ölçüme DAHİL EDİLMEYEN ısınma süresi.</param>
public sealed record LoadProfile(string Name, int Concurrency, TimeSpan Duration, TimeSpan Warmup);

/// <param name="ErrorRate">0..1 — hata sayısı / toplam istek.</param>
public sealed record LoadResult(
    string Name,
    int Concurrency,
    long Ok,
    long Failed,
    double Rps,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    IReadOnlyDictionary<string, int> Errors)
{
    public long Total => Ok + Failed;
    public double ErrorRate => Total == 0 ? 0 : (double)Failed / Total;
}

/// <summary>
/// Elle yazılmış yük koşum takımı.
///
/// <b>Neden hazır araç değil:</b> NBomber v6 kurumsal kullanımda ücretli aboneliğe bağlı
/// ve Poyra herkese açık bir depoda duruyor — paketi eklemek, Poyra'yı self-host eden
/// her kuruma sessizce lisans yükümlülüğü bindirirdi. k6 lisans açısından uygun ama
/// yalnız HTTP konuşur: rota karar çekirdeğinin (DecideCore) darboğaz OLMADIĞINI
/// kanıtlayan süreç içi senaryo onunla yazılamazdı.
///
/// <b>Ölçüm disiplini:</b>
///  - Isınma ölçüme girmez. İlk istekler EF model kurulumu, JIT ve bağlantı havuzu
///    açılışını taşır; ölçüme katılsalardı p99 tamamen o birkaç isteğin olurdu.
///  - Gecikme her isteğin KENDİ Stopwatch'ıyla ölçülür, ortalama değil yüzdelik raporlanır:
///    ödeme sisteminde "ortalama 40 ms" cümlesi, isteklerin %1'inin 3 saniye sürdüğünü gizler.
///  - Hatalar yutulmaz, tipine göre sayılır ve raporda görünür. Hata oranı yüksekken
///    çıkan yüksek RPS, sistemin hızlı olduğunu değil hızlı reddettiğini gösterir.
/// </summary>
public static class LoadHarness
{
    public static async Task<LoadResult> RunAsync(
        LoadProfile profile, Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        // Isınma: sonuçları atılır, yalnız sistemi sıcak hâle getirir
        if (profile.Warmup > TimeSpan.Zero)
            await DriveAsync(profile.Concurrency, profile.Warmup, action, collect: null, ct);

        var latencies = new ConcurrentLatencyBag();
        var elapsed = await DriveAsync(profile.Concurrency, profile.Duration, action, latencies, ct);

        return latencies.Summarize(profile.Name, profile.Concurrency, elapsed);
    }

    /// <summary>
    /// Sabit sayıda worker'ı süre dolana kadar döndürür. Worker'lar birbirini beklemez:
    /// yavaş bir istek yalnız kendi worker'ını tutar, diğerleri akmaya devam eder —
    /// gerçek trafik de böyle davranır.
    /// </summary>
    private static async Task<TimeSpan> DriveAsync(
        int concurrency, TimeSpan duration, Func<CancellationToken, Task> action,
        ConcurrentLatencyBag? collect, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(duration);

        var started = Stopwatch.GetTimestamp();

        var workers = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            while (!deadline.IsCancellationRequested)
            {
                var requestStart = Stopwatch.GetTimestamp();
                try
                {
                    await action(deadline.Token);
                    collect?.Add(Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds, error: null);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    // Süre doldu — yarım kalan istek HATA DEĞİLDİR, sayıma girmez.
                    // Sayılsaydı her koşu, worker sayısı kadar sahte hatayla biterdi.
                    return;
                }
                catch (Exception ex)
                {
                    collect?.Add(
                        Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds,
                        error: ex.GetType().Name);
                }
            }
        }).ToArray();

        await Task.WhenAll(workers);
        return Stopwatch.GetElapsedTime(started);
    }
}

/// <summary>
/// Kilitsiz olmayan ama basit toplayıcı: worker başına ayrı liste tutup sonda birleştirmek
/// yerine tek kilit kullanılır. Kilit maliyeti ölçülen işin (HTTP + Postgres) yanında
/// ihmal edilebilir; erken optimizasyon burada ölçümü karmaşıklaştırmaktan başka işe yaramaz.
/// </summary>
internal sealed class ConcurrentLatencyBag
{
    private readonly Lock _gate = new();
    private readonly List<double> _latencies = [];
    private readonly Dictionary<string, int> _errors = new(StringComparer.Ordinal);
    private long _ok;
    private long _failed;

    public void Add(double ms, string? error)
    {
        lock (_gate)
        {
            _latencies.Add(ms);
            if (error is null)
            {
                _ok++;
                return;
            }

            _failed++;
            _errors[error] = _errors.GetValueOrDefault(error) + 1;
        }
    }

    public LoadResult Summarize(string name, int concurrency, TimeSpan elapsed)
    {
        lock (_gate)
        {
            _latencies.Sort();
            var total = _ok + _failed;

            return new LoadResult(
                name, concurrency, _ok, _failed,
                Rps: elapsed.TotalSeconds <= 0 ? 0 : total / elapsed.TotalSeconds,
                P50Ms: Percentile(_latencies, 0.50),
                P95Ms: Percentile(_latencies, 0.95),
                P99Ms: Percentile(_latencies, 0.99),
                MaxMs: _latencies.Count == 0 ? 0 : _latencies[^1],
                Errors: new Dictionary<string, int>(_errors, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// En yakın sıra (nearest-rank) yüzdeliği: sıralı listede ceil(p·N)'inci değer.
    /// Ara değer üretilmez — ölçülen gerçek bir isteğin süresi raporlanır.
    /// </summary>
    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
            return 0;

        var rank = (int)Math.Ceiling(p * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
