namespace Poyra.Modules.Routing.Contracts;

/// <param name="Bin">Kartın ilk 6-8 hanesi (PAN DEĞİL) — bilinmiyorsa null.</param>
/// <param name="BankCode">Kartı çıkaran banka kodu — "on-us" kuralları için.</param>
/// <param name="Program">bonus / world / maximum / axess / paraf / bankkart …</param>
/// <param name="Country">
/// Kartı çıkaran ülke (ISO-3166 alpha-2) — "yurt dışı kart → Stripe/Adyen" kuralları için.
/// null = BİLİNMİYOR (BIN katalogda yok ya da kart henüz girilmedi); ülke kuralları
/// eşleşmez. Bilinmeyeni "yabancı" saymak, katalogda eksik kalan bir TR BIN'ini yurt
/// dışı rotasına yollardı — o rotada taksit yoktur, vade farkı sessizce kaybolurdu.
/// </param>
public sealed record CardFacts(
    string? Bin,
    string? BankCode,
    string? Program,
    string? Brand,
    string? CardType,
    bool IsCommercial,
    string? Country = null);

/// <summary>
/// Ödemenin Poyra'ya hangi yoldan girdiği. Kanal tahsilat davranışını değiştirir —
/// saha temsilcisinin müşterisi başka, gece koşan abonelik yenilemesi başka davranır —
/// ama rota kararı bunu bugüne dek göremiyordu.
/// Bu, işyerinin kendi sitesi/uygulaması ayrımı DEĞİLDİR: ikisi de aynı API'den girer ve
/// Poyra'ya "api" görünür. Kanal, Poyra'nın kendi bildiği ve doğrulayabildiği şeydir.
/// </summary>
public static class PaymentChannels
{
    /// <summary>İşyeri sunucusu POST /v1/payments çağırdı.</summary>
    public const string Api = "api";

    /// <summary>Ödeme linki checkout sayfasında ödendi.</summary>
    public const string Link = "link";

    /// <summary>Saha temsilcisinin ürettiği bağlantı ödendi.</summary>
    public const string Field = "field";

    /// <summary>Abonelik yenilemesi / yeniden tahsilat — kart token'ıyla, müşteri ekranda değil.</summary>
    public const string Subscription = "subscription";

    public static bool IsKnown(string? channel)
        => channel is Api or Link or Field or Subscription;
}

/// <param name="Seed">Deterministik hacim bölüşümü için tohum (intent id) — Math.Random yok.</param>
/// <param name="HourLocal">Türkiye saati (UTC+3) — "mesai dışı" tarzı kurallar için.</param>
/// <param name="Channel">
/// Ödemenin geldiği kanal (bkz. <see cref="PaymentChannels"/>). null = BİLİNMİYOR:
/// kanal alanı eklenmeden önce yazılmış kayıtlar böyledir ve kanal kuralları onlarda
/// eşleşmez. Eski kayıtları "api sayalım" demek geçmişe uydurma kanal atfetmek olurdu —
/// simülatör onları kanal kuralına yanlışlıkla sokar ve tasarruf tahminini bozardı.
/// (Kart sinyalleriyle aynı duruş.)
/// </param>
public sealed record RoutingFacts(
    Guid Seed,
    long AmountMinor,
    string Currency,
    int Installments,
    int HourLocal,
    CardFacts? Card = null,
    string? Channel = null);

/// <param name="AccountId">Aday hesap.</param>
/// <param name="Label">Panelde/gerekçede okunur ad.</param>
/// <param name="ExpectedCostMinor">Beklenen komisyon (anlaşma oranından) — bilinmiyorsa null.</param>
/// <param name="AuthRate">Son penceredeki başarı oranı (0..1) — örnek yetersizse null.</param>
/// <summary>
/// Bir hesabın bu dönemdeki hacim taahhüdü ve nerede olduğu.
/// </summary>
/// <param name="TargetMinor">Dönem için bankaya söz verilen hacim.</param>
/// <param name="AchievedMinor">Dönem başından beri o hesaba GERÇEKTEN giden hacim.</param>
/// <param name="DaysLeft">Dönemin bitimine kalan gün (bugün dahil, en az 1).</param>
public sealed record CommitmentProgress(long TargetMinor, long AchievedMinor, int DaysLeft)
{
    /// <summary>Kapatılması gereken açık; taahhüt tutmuşsa 0.</summary>
    public long GapMinor => Math.Max(0, TargetMinor - AchievedMinor);

    /// <summary>
    /// Açığı kalan günlere bölen aciliyet: "günde ne kadar geçmesi gerekiyor".
    /// Sıralama bunu kullanır — 18 günde 120.000 ₺, 3 günde 60.000 ₺'den daha az aciledir.
    /// </summary>
    public double RequiredDailyMinor => (double)GapMinor / Math.Max(1, DaysLeft);
}

/// <param name="MedianLatencyMs">Ölçülen gecikme ortancası — bilinmiyorsa null.</param>
/// <param name="Commitment">
/// Hacim taahhüdü ilerlemesi — tanımlı değilse null. Taahhüt stratejisi bunu okur;
/// diğer stratejiler görmezden gelir.
/// </param>
public sealed record RoutingCandidate(
    Guid AccountId,
    string Label,
    long? ExpectedCostMinor,
    double? AuthRate,
    int? MedianLatencyMs,
    CommitmentProgress? Commitment = null);

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
    /// <param name="cardBank">
    /// Kartı çıkaran banka kodu — bankaya özel (on-us) oran varsa o seçilir, yoksa genel oran.
    /// Kart bilinmiyorsa (hosted akışta müşteri henüz kart girmemişken) null geçilir ve
    /// genel oran kullanılır: on-us varsayıp ucuz oran uydurmak, rotayı gerçekte daha
    /// pahalı olan POS'a yönlendirirdi.
    /// </param>
    Task<IReadOnlyList<ConnectorCommissionRate>> GetRatesAsync(
        int installmentCount, string? cardBank, CancellationToken ct);
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

/// <param name="VolumeMinor">Dönem başından beri hesaba giden TAHSİL EDİLMİŞ hacim.</param>
public sealed record ConnectorVolume(Guid ConnectorAccountId, long VolumeMinor);

/// <summary>
/// Hacim taahhüdünün ilerleme sinyali: dönem içinde her hesaba gerçekte ne kadar iş
/// gitti (Payments uygular — bağımlılık tersine, maliyet/performans portlarıyla aynı yön).
/// Sayım TAHSİL EDİLMİŞ işlemler üzerinden yapılır: banka taahhüdü başarısız denemelerle
/// değil, geçen ciroyla ölçer.
/// </summary>
public interface IVolumeProgressSource
{
    Task<IReadOnlyList<ConnectorVolume>> GetAsync(DateTimeOffset periodStart, CancellationToken ct);
}
