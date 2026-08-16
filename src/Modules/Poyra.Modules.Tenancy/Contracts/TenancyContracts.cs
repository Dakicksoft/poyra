namespace Poyra.Modules.Tenancy.Contracts;


public interface ITenantDirectory
{
    Task<IReadOnlyList<Guid>> GetActiveTenantIdsAsync(CancellationToken ct);
}

/// <param name="DisplayName">Checkout'ta gösterilecek satıcı adı (işyeri adına düşer).</param>
/// <param name="LogoUrl">null = logo yüklenmemiş; sayfa yalnız adı gösterir.</param>
public sealed record CheckoutBranding(
    string DisplayName,
    string PrimaryColor,
    string? LogoUrl,
    string? SupportEmail,
    string? SupportPhone);

/// <summary>
/// Checkout host'unun (kimliksiz) işyeri markasını okuma kapısı. Marka, ÖDEME
/// SAYFASININ kimliğidir: müşteri tanımadığı bir marka görünce ödemeyi bırakır.
/// </summary>
public interface ITenantBrandingSource
{
    Task<CheckoutBranding> GetAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Logo baytları — checkout kendi uç noktasından servis eder.</summary>
    Task<(byte[] Bytes, string ContentType)?> GetLogoAsync(Guid tenantId, CancellationToken ct);
}
