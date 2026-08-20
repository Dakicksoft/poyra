namespace Poyra.Modules.Routing.Contracts;

/// <param name="ActualAccountId">İşlemin GERÇEKTE gittiği hesap (ilk deneme).</param>
/// <param name="ActualCostMinor">O hesabın anlaşma oranından beklenen komisyon.</param>
/// <param name="Channel">İşlemin geldiği kanal — kanal alanı eklenmeden önceki kayıtlarda null.
/// Replay'de null kalması ŞARTTIR: "api sayalım" demek kanal kuralını geçmişteki ödeme
/// linklerine de uygular ve tasarruf tahminini şişirirdi.</param>
/// <param name="Forced">Hesap elle sabitlendi (ForceConnectorAccountId) — kural devrede değildi;
/// kural değişse de işyeri zorlamaya devam edeceğinden replay'de "kayma" raporlanmamalı.</param>
public sealed record HistoricPayment(
    string PaymentId,
    Guid Seed,
    long AmountMinor,
    string Currency,
    int Installments,
    int HourLocal,
    CardFacts? Card,
    Guid ActualAccountId,
    long? ActualCostMinor,
    DateTimeOffset CreatedAt,
    bool Forced = false,
    string? Channel = null);

/// <summary>
/// Simülatörün geçmiş veri kaynağı — Payments uygular (bağımlılık tersine, bkz. RoutingContracts).
/// </summary>
public interface IHistoricPaymentSource
{
    Task<IReadOnlyList<HistoricPayment>> GetAsync(DateTimeOffset since, int limit, CancellationToken ct);
}

/// <summary>
/// Yürütme-anı uygunluk sinyali: confirm döngüsü karar zincirini yürütürken bazı hesapları
/// atlar (çözülemeyen hesap, taksit desteklemeyen konnektör, taksit şeması tanımsız hesap).
/// Simülatör aynı elemeyi uygulayabilsin diye Payments uygular (bağımlılık tersine, üstteki gibi) —
/// aksi hâlde simülasyon, gerçekte atlanacak bir POS'a "kayar" deyip tasarrufu şişirir.
/// </summary>
public interface IExecutionFeasibilitySource
{
    /// <summary>Verilen taksit + kart programı için işlemi GERÇEKTEN işleyebilecek hesaplar.</summary>
    Task<IReadOnlySet<Guid>> GetCapableAccountsAsync(int installments, string? program, CancellationToken ct);
}
