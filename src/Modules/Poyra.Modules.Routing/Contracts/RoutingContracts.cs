namespace Poyra.Modules.Routing.Contracts;

/// <param name="Bin">Kartın ilk 6-8 hanesi (PAN DEĞİL) — bilinmiyorsa null.</param>
/// <param name="BankCode">Kartı çıkaran banka kodu — "on-us" kuralları için.</param>
/// <param name="Program">bonus / world / maximum / axess / paraf / bankkart …</param>
public sealed record CardFacts(
    string? Bin,
    string? BankCode,
    string? Program,
    string? Brand,
    string? CardType,
    bool IsCommercial);

/// <param name="Seed">Deterministik hacim bölüşümü için tohum (intent id) — Math.Random yok.</param>
/// <param name="HourLocal">Türkiye saati (UTC+3) — "mesai dışı" tarzı kurallar için.</param>
public sealed record RoutingFacts(
    Guid Seed,
    long AmountMinor,
    string Currency,
    int Installments,
    int HourLocal,
    CardFacts? Card = null);

/// <param name="AccountId">Aday hesap.</param>
/// <param name="Label">Panelde/gerekçede okunur ad.</param>
/// <param name="ExpectedCostMinor">Beklenen komisyon (anlaşma oranından) — bilinmiyorsa null.</param>
/// <param name="AuthRate">Son penceredeki başarı oranı (0..1) — örnek yetersizse null.</param>
/// <param name="MedianLatencyMs">Ölçülen gecikme ortancası — bilinmiyorsa null.</param>
public sealed record RoutingCandidate(
    Guid AccountId,
    string Label,
    long? ExpectedCostMinor,
    double? AuthRate,
    int? MedianLatencyMs);

/// <summary>
/// Sıralı adaylar + insan-okur gerekçe ("neden bu POS") + kararın dayandığı sinyaller.
/// Gerekçe ve sinyaller intent'e yazılır, panelde gösterilir — açıklanabilir yönlendirme.
/// </summary>
public sealed record RoutingDecision(
    IReadOnlyList<Guid> AccountIds,
    string Reason,
    int MaxAttempts,
    string? RuleName,
    int? RuleVersion,
    string Strategy = "priority",
    IReadOnlyList<RoutingCandidate>? Candidates = null);

public interface IRoutingService
{
    Task<RoutingDecision> DecideAsync(RoutingFacts facts, CancellationToken ct);
}

// --- Rota motorunun ihtiyaç duyduğu dış sinyaller ---------------------------
// Bağımlılık tersine çevrilmiştir: portları TÜKETEN (Routing) tanımlar, sağlayan
// modüller (Payments, Recon) uygular. Aksi hâlde Payments→Routing→Payments döngüsü
// oluşurdu; bu yön aynı zamanda "rota motoru kimseyi tanımaz" ilkesini korur.

/// <param name="RateBps">Anlaşılan işyeri komisyonu (‱): 250 = %2,50.</param>
public sealed record ConnectorCommissionRate(Guid ConnectorAccountId, int InstallmentCount, int RateBps);

/// <summary>
/// Maliyet sinyali. Kaynak, mutabakatın komisyon anlaşmalarıdır — rotanın kullandığı oran,
/// ay sonunda bankaya itiraz ederken kullanılan oranla AYNIDIR (tek doğruluk kaynağı).
/// </summary>
public interface ICommissionRateSource
{
    Task<IReadOnlyList<ConnectorCommissionRate>> GetRatesAsync(int installmentCount, CancellationToken ct);
}

/// <param name="AuthRate">0..1 — tamamlanan denemelerde başarı oranı.</param>
/// <param name="MedianLatencyMs">Ölçülen çağrıların ortancası (p50).</param>
/// <param name="SampleSize">Örnek sayısı — eşik altındaysa sinyal güvenilmez sayılır.</param>
public sealed record ConnectorPerformance(
    Guid ConnectorAccountId, double AuthRate, int MedianLatencyMs, int SampleSize);

/// <summary>Performans sinyali: son penceredeki gerçek işlem sonuçları (Payments uygular).</summary>
public interface IConnectorPerformanceSource
{
    Task<IReadOnlyList<ConnectorPerformance>> GetAsync(TimeSpan window, CancellationToken ct);
}
