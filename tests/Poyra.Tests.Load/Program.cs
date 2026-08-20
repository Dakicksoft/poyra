using System.Globalization;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Routing.Dsl;
using Poyra.Modules.Routing.Infrastructure;
using Poyra.Tests.Load;

// Poyra yük profili.
//
// CI'ın normal akışında KOŞMAZ (konsol uygulaması — `dotnet test` görmez). Elle:
//   dotnet run --project tests/Poyra.Tests.Load -- [senaryo] [--sure 30] [--es 32]
//
// Amaç mutlak rakam yayımlamak değil, iki soruya kanıt üretmek:
//   1. Rota karar çekirdeği darboğaz mı? (süreç içi, I/O yok)
//   2. Yazma yolu eşzamanlılık altında ne yapıyor? (RLS + olay defteri + outbox dahil)

// Senaryo adı YALNIZ ilk argüman olabilir. "İlk bayrak olmayan argüman" desek,
// `--sure 10` yazıldığında "10" senaryo adı sanılır ve hiçbir şey koşmaz.
var scenarioFilter = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
    ? args[0]
    : null;
var duration = TimeSpan.FromSeconds(ArgValue("--sure", 20));
var concurrency = ArgValue("--es", 32);
var warmup = TimeSpan.FromSeconds(ArgValue("--isinma", 5));

Console.WriteLine($"Poyra yük profili — süre {duration.TotalSeconds:0}s · eşzamanlılık {concurrency} "
                  + $"· ısınma {warmup.TotalSeconds:0}s");
Console.WriteLine();

var results = new List<LoadResult>();

// ---------------------------------------------------------------- 1) rota karar çekirdeği
//
// Saf, DB'siz, ağsız. Bu senaryo "Poyra kaç işlem kaldırır" sorusuna DEĞİL, "karar
// mantığı darboğaz mı" sorusuna cevap verir. Rota kararı her ödemede bir kez koşar;
// burada milisaniyelerce sürüyorsa hiçbir altyapı iyileştirmesi onu kurtaramaz.
if (Matches("rota-karari"))
{
    var document = RuleDocument.Parse("""
        {
          "strategy": "balanced",
          "rules": [
            { "name": "on-us", "when": { "fact": "card.bank", "op": "eq", "value": "0062" },
              "route": ["Garanti POS"] },
            { "name": "yüksek tutar", "when": { "fact": "amount_minor", "op": "gte", "value": 500000 },
              "strategy": "best_success" }
          ],
          "fallback": ["Yedek POS"],
          "guards": { "maxAttempts": 3 }
        }
        """);

    var candidates = Enumerable.Range(0, 8).Select(i => new RoutingCandidate(
        Guid.CreateVersion7(), $"POS {i}", 200 + i * 30, 0.90 + i * 0.005, 150 + i * 20)).ToList();

    var facts = new RoutingFacts(
        Guid.CreateVersion7(), 250_00, "TRY", 1, 14,
        new CardFacts("540061", "0064", "maximum", "visa", "credit", false, "TR"),
        PaymentChannels.Api);

    results.Add(await LoadHarness.RunAsync(
        new LoadProfile("rota-karari (süreç içi)", concurrency, duration, warmup),
        _ =>
        {
            // Her istek yeni bir tohum alır: hacim bölüşümü ve ölçüm kotası kovaları
            // gerçek trafikteki gibi dağılsın, tek bir dala saplanmasın.
            var decision = RoutingEngine.DecideCore(
                document, facts with { Seed = Guid.CreateVersion7() }, candidates);

            if (decision.AccountIds.Count == 0)
                throw new InvalidOperationException("rota boş döndü");

            return Task.CompletedTask;
        }));
}

// ---------------------------------------------------------------- HTTP senaryoları
var httpScenarios = new[] { "odeme-olustur", "odeme-confirm" }.Where(Matches).ToArray();
if (httpScenarios.Length > 0)
{
    Console.WriteLine("Zemin kuruluyor (Postgres + Kestrel)…");
    await using var environment = await LoadEnvironment.StartAsync();
    var tenant = await environment.SeedTenantAsync("Yük Testi A.Ş.");
    Console.WriteLine($"Hazır: {environment.ApiAdres}");
    Console.WriteLine();

    // 2) Yazma yolu: niyet + olay defteri + RLS. Rota ve konnektör devrede değil.
    if (httpScenarios.Contains("odeme-olustur"))
    {
        results.Add(await LoadHarness.RunAsync(
            new LoadProfile("odeme-olustur (yazma yolu)", concurrency, duration, warmup),
            async ct => await environment.PostAsync<object>("/v1/payments",
                new { amountMinor = 149_00, currency = "TRY" },
                ("X-Api-Key", tenant.ApiKey))));
    }

    // 3) Tam akış: rota kararı + MockBank initiate + deneme + tek kullanımlık callback
    //    belirteci. Banka çağrısı MockBank'tır — gerçek bankanın ağ gecikmesi YOKTUR,
    //    yani bu rakam Poyra'nın kendi maliyetidir, uçtan uca müşteri deneyimi değil.
    if (httpScenarios.Contains("odeme-confirm"))
    {
        results.Add(await LoadHarness.RunAsync(
            new LoadProfile("odeme-confirm (tam akış)", concurrency, duration, warmup),
            async ct => await environment.PostAsync<object>("/v1/payments",
                new { amountMinor = 149_00, currency = "TRY", confirm = true },
                ("X-Api-Key", tenant.ApiKey))));
    }
}

if (results.Count == 0)
{
    Console.Error.WriteLine(
        $"'{scenarioFilter}' hiçbir senaryoyla eşleşmedi. "
        + "Senaryolar: rota-karari · odeme-olustur · odeme-confirm");
    return 2;
}

Report(results);

// Hata oranı eşiği: yük altında hata YOKSA rakam anlamlıdır. %1'in üstünde hata varken
// yüksek RPS, sistemin hızlı olduğunu değil hızlı REDDETTİĞİNİ gösterir — o yüzden
// koşum başarısız sayılır ve CI'da kırmızı yanar.
var broken = results.Where(r => r.ErrorRate > 0.01).ToList();
if (broken.Count == 0)
    return 0;

Console.Error.WriteLine();
foreach (var result in broken)
    Console.Error.WriteLine($"HATA EŞİĞİ AŞILDI: {result.Name} — %{result.ErrorRate * 100:0.00}");

return 1;

int ArgValue(string name, int fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length
           && int.TryParse(args[index + 1], CultureInfo.InvariantCulture, out var value)
        ? value
        : fallback;
}

bool Matches(string scenario)
    => scenarioFilter is null || scenario.Contains(scenarioFilter, StringComparison.OrdinalIgnoreCase);

static void Report(IReadOnlyList<LoadResult> results)
{
    var tr = CultureInfo.GetCultureInfo("tr-TR");

    // Alt-milisaniye değerler "0,0ms" diye ezilmemeli: rota çekirdeği tam olarak orada
    // yaşıyor ve "0" görmek "ölçemedik" ile "çok hızlı"yı ayırt edilemez hâle getirir.
    string Ms(double ms) => ms switch
    {
        < 1 => string.Create(tr, $"{ms * 1000:N0}µs"),
        < 100 => string.Create(tr, $"{ms:N1}ms"),
        _ => string.Create(tr, $"{ms:N0}ms"),
    };

    Console.WriteLine();
    Console.WriteLine(
        $"{"Senaryo",-30} {"İstek",9} {"RPS",10} {"p50",9} {"p95",9} {"p99",9} {"maks",9} {"hata",8}");
    Console.WriteLine(new string('-', 100));

    foreach (var r in results)
    {
        Console.WriteLine(string.Create(tr,
            $"{r.Name,-30} {r.Total,9:N0} {r.Rps,10:N0} {Ms(r.P50Ms),9} {Ms(r.P95Ms),9} "
            + $"{Ms(r.P99Ms),9} {Ms(r.MaxMs),9} {r.ErrorRate,8:P1}"));

        foreach (var (type, count) in r.Errors.OrderByDescending(e => e.Value))
            Console.WriteLine($"{"",-30} └─ {type}: {count}");
    }

    Console.WriteLine();
    Console.WriteLine("Koşulun sınırları — rakamlar bu kayıtlarla birlikte okunmalı:");
    Console.WriteLine("  · MockBank sanal POS'u kullanılır; gerçek bankanın ağ gecikmesi YOKTUR.");
    Console.WriteLine("    Ölçülen, Poyra'nın kendi maliyetidir — uçtan uca müşteri deneyimi değil.");
    Console.WriteLine("  · Yük üreteci uygulamayla AYNI süreçte koşar ve WebApplicationFactory");
    Console.WriteLine("    sözleşmesi gereği iki host açılır (2×4 Hangfire worker). İkisi de");
    Console.WriteLine("    ölçümü YAVAŞLATIR — sapma tek yönlüdür, rakamlar muhafazakârdır.");
    Console.WriteLine("  · Postgres tek kullanımlık kapsayıcıda (ayarsız). Gerçek sunucu için");
    Console.WriteLine("    POYRA_LOAD_CS ile dışarıdan bağlantı verin.");
}
