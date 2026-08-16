namespace Poyra.Modules.Ledger.Contracts;

/// <summary>Deftere yazılacak tahsilat — ödeme modülünün defterin anladığı dile çevrilmiş hâli.</summary>
/// <param name="AttemptPublicId">att_… — bankaya giden sipariş no; alacağın tekil anahtarı.</param>
/// <param name="GrossMinor">Karttan ÇEKİLEN tutar (vade farkı dahil).</param>
public sealed record CapturedCharge(
    string AttemptPublicId,
    string? PaymentPublicId,
    Guid ConnectorAccountId,
    long GrossMinor,
    string Currency,
    int Installments,
    DateTimeOffset CapturedAtServer);

/// <summary>
/// Defterin tahsilat kaynağı. <b>Port BURADA tanımlıdır</b> (bağımlılığın tersine
/// çevrilmesi): defter Ödemeler'e bakmaz, Ödemeler bu arayüzü uygular. Aksi hâlde
/// Payments → Ledger → Payments çevrimi oluşurdu.
/// </summary>
public interface ICaptureFeed
{

    Task<IReadOnlyList<CapturedCharge>> GetCapturedSinceAsync(
        DateTimeOffset sinceInclusive, int limit, CancellationToken ct);

    Task<IReadOnlyList<CapturedCharge>> GetRefundedSinceAsync(
        DateTimeOffset sinceInclusive, int limit, CancellationToken ct);
}

/// <param name="RateBps">Anlaşılan komisyon oranı (250 = %2,50).</param>
/// <param name="ValorDays">Hesaba geçiş için anlaşılan İŞ GÜNÜ sayısı.</param>
public sealed record CommissionTerm(int RateBps, int ValorDays);

/// <summary>
/// Beklenen tutarın dayanağı: işyerinin bankayla anlaştığı oran ve valör.
/// Mutabakat modülü (M14) uygular — anlaşmalar orada yaşar.
///
/// Anlaşma yoksa <c>null</c> döner ve alacak "beklenen tutarı bilinmiyor" olarak
/// yazılır. <b>Tahmin üretilmez:</b> uydurulmuş bir komisyon, olmayan bir farkı
/// varmış gibi gösterir ve bankaya haksız itiraz yazdırır.
/// </summary>
public interface ICommissionTerms
{
    Task<CommissionTerm?> FindAsync(Guid connectorAccountId, int installments, CancellationToken ct);
}

/// <summary>
/// Mutabakatın deftere "banka bu alacağı kabul etti" demesi için kullandığı kapı.
///
/// Yön önemlidir: <b>mutabakat deftere haber verir</b>, defter mutabakata sormaz.
/// Böylece defter hiçbir modüle bağlı kalmaz.
/// </summary>
public interface IReceivableConfirmation
{
    Task ConfirmAsync(
        string attemptPublicId, long confirmedCommissionMinor, DateOnly? confirmedValueDate,
        CancellationToken ct);
}
