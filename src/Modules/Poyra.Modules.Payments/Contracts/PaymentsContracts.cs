namespace Poyra.Modules.Payments.Contracts;

public sealed record LedgerAttempt(
    Guid AttemptId,
    string PublicId, // att_… — bankaya giden sipariş no; ekstre satırının anahtarı
    Guid ConnectorAccountId,
    long ChargedAmountMinor,
    int Installments,
    DateTimeOffset? CapturedAt);

public sealed record LedgerRefund(Guid RefundId, string PublicId, long AmountMinor);

public sealed record ChargeResult(
    string PaymentId, bool Success, string? UnifiedCode, string? RawCode, string? Message);


public interface IPaymentInitiator
{

    Task<ChargeResult> ChargeWithTokenAsync(
        long amountMinor, string currency, string cardToken, string? description,
        string? customerRef, CancellationToken ct);
}

public interface IPaymentLedger
{
    /// <summary>Ekstre satırındaki sipariş numarasıyla tahsil edilmiş denemeyi bulur.</summary>
    Task<LedgerAttempt?> FindCapturedByOrderIdAsync(string orderId, CancellationToken ct);

    /// <summary>Bir hesabın o TR günü tahsil edilen denemeleri — "ekstrede eksik" taraması için.</summary>
    Task<IReadOnlyList<LedgerAttempt>> GetCapturedForDayAsync(
        Guid connectorAccountId, DateOnly dayTr, CancellationToken ct);

    /// <summary>İade ekstre satırı eşleştirmesi: siparişin başarılı iadeleri.</summary>
    Task<IReadOnlyList<LedgerRefund>> GetSucceededRefundsByAttemptOrderIdAsync(
        string orderId, CancellationToken ct);
}

public sealed record PaymentSummary(
    Guid PaymentIntentId, string PublicId, long AmountMinor, string Currency, bool IsCaptured);

public interface IPaymentLookup
{
    Task<PaymentSummary?> FindByPublicIdAsync(string publicId, CancellationToken ct);
}


/// <param name="Flow">"hosted" (banka 3DS sayfası) veya "direct" (kart bizde, 3DS yok).</param>
/// <param name="MaskedPan">İlk 6 + son 4 — PCI dışıdır ve "aynı kart" sinyali için yeterlidir.</param>
public sealed record RiskContext(
    string PaymentId,
    long AmountMinor,
    string Currency,
    int Installments,
    string Flow,
    string? CustomerRef,
    string? IpAddress,
    string? Country,
    string? Bin,
    string? BankCode,
    string? Program,
    string? Brand,
    string? CardType,
    bool? IsCommercial,
    string? MaskedPan);

public static class RiskOutcomes
{
    public const string Allow = "allow";

    public const string Challenge = "challenge";

    public const string Review = "review";

    public const string Block = "block";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Allow, Challenge, Review, Block };
}

public sealed record RiskDecision(
    string Outcome,
    string? RuleName = null,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Signals = null)
{
    public static RiskDecision Allowed { get; } = new(RiskOutcomes.Allow);

    public bool Blocks => Outcome == RiskOutcomes.Block;
    public bool RequiresThreeDs => Outcome == RiskOutcomes.Challenge;
}


public interface IRiskGate
{
    Task<RiskDecision> AssessAsync(RiskContext context, CancellationToken ct);
}

public sealed record VelocitySnapshot(
    int Attempts1h,
    int Attempts24h,
    int Declines1h,
    long Amount24hMinor,
    int DistinctCards24h);

public interface IPaymentVelocitySource
{
    Task<VelocitySnapshot> GetAsync(
        string? customerRef, string? ipAddress, string? maskedPan, DateTimeOffset now, CancellationToken ct);
}


public sealed record CustomerPayment(
    string PaymentId, long AmountMinor, string Currency, string Status,
    int Installments, string? MaskedPan, DateTimeOffset CreatedAt);

public sealed record CustomerPaymentTotals(int Count, long SucceededMinor, long RefundedMinor);


public interface ICustomerPaymentSource
{
    Task<IReadOnlyList<CustomerPayment>> GetPaymentsAsync(string customerRef, int limit, CancellationToken ct);

    Task<CustomerPaymentTotals> GetTotalsAsync(string customerRef, CancellationToken ct);
}
