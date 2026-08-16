using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Tenancy.Domain;

public sealed class Organization : IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum TenantStatus
{
    Active,
    Suspended,
}

public sealed class Tenant : IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid OrganizationId { get; init; }
    public required string Name { get; set; }
    public required string Slug { get; init; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;

    /// <summary>
    /// Owner/admin rolleri için iki adımlı doğrulama zorunlu mu? Zorunluysa ve kullanıcı
    /// henüz kurmadıysa giriş ENGELLENMEZ (sahibi kilitlemek onarımı imkânsız kılar) —
    /// oturum açılır ve kullanıcı kuruluma yönlendirilir.
    /// </summary>
    public bool RequireTotpForPrivileged { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>İş profili: kanal/marka (web, mobil, bayi).</summary>
public sealed class BusinessProfile : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public required string Name { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// API anahtarı. Düz metin yalnız oluşturma anında bir kez gösterilir; burada SHA-512 özeti durur.
/// Bilinçli olarak ITenantOwned DEĞİL: işyeri çözümlemesi bu tablo ÜZERİNDEN yapılır
/// (tavuk-yumurta) — RLS'siz platform tablosudur, erişim yalnız hash eşitliğiyle olur.
/// </summary>
public sealed class ApiKey : IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public required string Name { get; set; }
    public required string PrefixHint { get; init; }
    public required string KeyHash { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
