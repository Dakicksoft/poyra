namespace Poyra.Modules.PaymentLinks.Contracts;

public sealed record CheckoutLink(
    Guid TenantId,
    Guid LinkId,
    string PublicId,
    string Slug,
    long? AmountMinor,
    string Currency,
    string Description,
    int MaxInstallments,
    string? UnavailableReason);

public interface ICheckoutLinkResolver
{
    Task<CheckoutLink?> ResolveAsync(string slug, CancellationToken ct);

    Task RegisterSuccessAsync(
        Guid tenantId, Guid linkId, string paymentPublicId, long amountMinor, CancellationToken ct);

    Task RegisterAttemptAsync(
        Guid tenantId, Guid linkId, string paymentPublicId, long amountMinor, CancellationToken ct);
}
