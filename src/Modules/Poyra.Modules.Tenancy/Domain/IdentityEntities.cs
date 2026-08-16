using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Tenancy.Domain;

public sealed class User : IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Email { get; init; } // her zaman küçük harf
    public required string PasswordHash { get; set; }
    public required string DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? EmailVerifiedAt { get; set; }

    /// <summary>
    /// TOTP (RFC 6238) ikinci faktör. Sır AES-GCM korumalı saklanır — veritabanını okuyan
    /// biri kod üretemesin. Kurulum iki aşamalıdır: sır önce beklemeye yazılır, kullanıcı
    /// uygulamadan ilk kodu girip kanıtlayınca etkinleşir (yanlış kurulan 2FA kilitler).
    /// </summary>
    public byte[]? TotpSecretProtected { get; set; }

    public byte[]? TotpPendingSecretProtected { get; set; }
    public DateTimeOffset? TotpEnabledAt { get; set; }
    public bool TotpEnabled => TotpEnabledAt is not null;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum UserTokenPurpose
{
    PasswordReset,
    EmailVerification,

    /// <summary>2FA kurtarma kodu — telefon kaybında tek kullanımlık giriş.</summary>
    TotpRecovery,

    /// <summary>"Bu cihazı hatırla" — 30 gün ikinci adım sorulmaz.</summary>
    TotpDevice,
}

public static class UserTokenPurposeMap
{
    public static readonly IReadOnlyDictionary<UserTokenPurpose, string> ToDb =
        new Dictionary<UserTokenPurpose, string>
        {
            [UserTokenPurpose.PasswordReset] = "password_reset",
            [UserTokenPurpose.EmailVerification] = "email_verification",
            [UserTokenPurpose.TotpRecovery] = "totp_recovery",
            [UserTokenPurpose.TotpDevice] = "totp_device",
        };

    public static readonly IReadOnlyDictionary<string, UserTokenPurpose> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

public sealed class UserToken : IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public UserTokenPurpose Purpose { get; init; }
    public required string TokenHash { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Yeni token üretilince eskiler burada iptal edilir (aynı anda tek geçerli bağlantı).</summary>
    public DateTimeOffset? InvalidatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsUsable(DateTimeOffset now)
        => UsedAt is null && InvalidatedAt is null && ExpiresAt > now;
}

public sealed class UserTenant : IAuditable
{
    public Guid UserId { get; init; }
    public Guid TenantId { get; init; }
    public TenantRole Role { get; set; } = TenantRole.Auditor;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Yenileme token (opak, prt_…). Yalnız SHA-512 özeti saklanır; her kullanımda
/// döndürülür (rotation) — eski token iptal edilir, zinciri replaced_by_hash tutar.
/// </summary>
public sealed class RefreshToken : IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public Guid TenantId { get; init; }
    public required string TokenHash { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
