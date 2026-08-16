namespace Poyra.Modules.Routing.Contracts;

/// <param name="ActualAccountId">İşlemin GERÇEKTE gittiği hesap (ilk deneme).</param>
/// <param name="ActualCostMinor">O hesabın anlaşma oranından beklenen komisyon.</param>
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
    DateTimeOffset CreatedAt);

/// <summary>
/// Simülatörün geçmiş veri kaynağı — Payments uygular (bağımlılık tersine, bkz. RoutingContracts).
/// </summary>
public interface IHistoricPaymentSource
{
    Task<IReadOnlyList<HistoricPayment>> GetAsync(DateTimeOffset since, int limit, CancellationToken ct);
}
