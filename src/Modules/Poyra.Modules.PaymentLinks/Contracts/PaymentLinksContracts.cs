namespace Poyra.Modules.PaymentLinks.Contracts;

/// <summary>
/// Bağlantıyı kimin ürettiği. Checkout ikisini de AYNI sayfadan sunar — ayrım yalnız
/// burada saklanır ve ödemenin kanalına taşınır; aksi hâlde saha tahsilatı rota
/// kararında normal ödeme linkinden ayırt edilemezdi.
/// </summary>
public static class PaymentLinkOrigins
{
    /// <summary>Panelden ya da API'den üretilen olağan ödeme linki.</summary>
    public const string Link = "link";

    /// <summary>Saha uygulamasının senkronunda üretilen tahsilat bağlantısı.</summary>
    public const string Field = "field";
}

/// <param name="Origin">bkz. <see cref="PaymentLinkOrigins"/>.</param>
public sealed record CheckoutLink(
    Guid TenantId,
    Guid LinkId,
    string PublicId,
    string Slug,
    long? AmountMinor,
    string Currency,
    string Description,
    int MaxInstallments,
    string? UnavailableReason,
    string Origin = PaymentLinkOrigins.Link);

public interface ICheckoutLinkResolver
{
    Task<CheckoutLink?> ResolveAsync(string slug, CancellationToken ct);

    Task RegisterSuccessAsync(
        Guid tenantId, Guid linkId, string paymentPublicId, long amountMinor, CancellationToken ct);

    Task RegisterAttemptAsync(
        Guid tenantId, Guid linkId, string paymentPublicId, long amountMinor, CancellationToken ct);
}
