using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Tenancy.Domain;

public sealed class TenantBranding : ITenantOwned, IAuditable
{
    public Guid TenantId { get; init; }

    public string? DisplayName { get; set; }

    public string? PrimaryColor { get; set; }

    public byte[]? LogoBytes { get; set; }
    public string? LogoContentType { get; set; }

    public string? SupportEmail { get; set; }
    public string? SupportPhone { get; set; }

    public string? CheckoutDomain { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Marka rengi geçerli değilse Poyra rengine düşer — bozuk CSS üretilmez.</summary>
    public string EffectiveColor =>
        PrimaryColor is { Length: 7 } c && c[0] == '#'
        && c[1..].All(ch => Uri.IsHexDigit(ch))
            ? c
            : "#C4713B";
}
