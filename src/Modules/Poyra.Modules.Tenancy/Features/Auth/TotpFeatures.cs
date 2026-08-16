using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Tenancy.Domain;
using Poyra.Modules.Tenancy.Security;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Security;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Tenancy.Features.Auth;

public sealed record TotpStatusDto(
    bool Enabled, DateTimeOffset? EnabledAt, bool PendingSetup, int RecoveryCodesLeft);

public sealed record TotpEnrollmentDto(string SecretBase32, string OtpauthUri);

public sealed record TotpRecoveryCodesDto(IReadOnlyList<string> Codes);

public sealed record TotpStatusQuery : IQuery<TotpStatusDto>;

public sealed class TotpStatusHandler(TenancyDbContext db, UserContext user, IClock clock)
    : IQueryHandler<TotpStatusQuery, TotpStatusDto>
{
    public async Task<TotpStatusDto> Handle(TotpStatusQuery query, CancellationToken ct)
    {
        var userId = user.UserId ?? throw new PoyraException(401, "auth.no_user", "Oturum yok.");
        var record = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);

        var now = clock.UtcNow;
        var recoveryLeft = await db.UserTokens.AsNoTracking().CountAsync(
            t => t.UserId == userId && t.Purpose == UserTokenPurpose.TotpRecovery
                 && t.UsedAt == null && t.InvalidatedAt == null && t.ExpiresAt > now, ct);

        return new TotpStatusDto(
            record.TotpEnabled, record.TotpEnabledAt,
            record.TotpPendingSecretProtected is not null, recoveryLeft);
    }
}

public sealed record BeginTotpEnrollmentCommand : ICommand<TotpEnrollmentDto>;

public sealed class BeginTotpEnrollmentHandler(
    TenancyDbContext db, UserContext user, ICredentialProtector protector)
    : ICommandHandler<BeginTotpEnrollmentCommand, TotpEnrollmentDto>
{
    public async Task<TotpEnrollmentDto> Handle(BeginTotpEnrollmentCommand command, CancellationToken ct)
    {
        var userId = user.UserId ?? throw new PoyraException(401, "auth.no_user", "Oturum yok.");
        var record = await db.Users.SingleAsync(u => u.Id == userId, ct);

        if (record.TotpEnabled)
            throw new PoyraException(409, "totp.already_enabled",
                "İki adımlı doğrulama zaten açık. Yeniden kurmak için önce kapatın.");

        var secret = Totp.GenerateSecret();
        record.TotpPendingSecretProtected = protector.Protect(
            new Dictionary<string, string> { ["totp"] = secret });
        await db.SaveChangesAsync(ct);

        return new TotpEnrollmentDto(secret, Totp.BuildOtpauthUri(record.Email, secret));
    }
}

public sealed record ConfirmTotpEnrollmentCommand(string Code) : ICommand<TotpRecoveryCodesDto>;

public sealed class ConfirmTotpEnrollmentHandler(
    TenancyDbContext db, UserContext user, ICredentialProtector protector, IClock clock)
    : ICommandHandler<ConfirmTotpEnrollmentCommand, TotpRecoveryCodesDto>
{
    public const int RecoveryCodeCount = 8;

    public async Task<TotpRecoveryCodesDto> Handle(
        ConfirmTotpEnrollmentCommand command, CancellationToken ct)
    {
        var userId = user.UserId ?? throw new PoyraException(401, "auth.no_user", "Oturum yok.");
        var record = await db.Users.SingleAsync(u => u.Id == userId, ct);

        if (record.TotpPendingSecretProtected is null)
            throw new PoyraException(409, "totp.no_pending",
                "Bekleyen kurulum yok — önce kurulumu başlatın.");

        var secret = protector.Unprotect(record.TotpPendingSecretProtected)["totp"];
        if (!Totp.Verify(secret, command.Code.Trim(), clock.UtcNow))
            throw new PoyraException(400, "totp.invalid_code",
                "Kod doğrulanamadı. Uygulamadaki güncel 6 haneli kodu girin.");

        record.TotpSecretProtected = record.TotpPendingSecretProtected;
        record.TotpPendingSecretProtected = null;
        record.TotpEnabledAt = clock.UtcNow;

        var codes = await RotateRecoveryCodesAsync(db, userId, clock.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        return new TotpRecoveryCodesDto(codes);
    }

    internal static async Task<IReadOnlyList<string>> RotateRecoveryCodesAsync(
        TenancyDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var old = await db.UserTokens
            .Where(t => t.UserId == userId && t.Purpose == UserTokenPurpose.TotpRecovery
                        && t.UsedAt == null && t.InvalidatedAt == null)
            .ToListAsync(ct);
        foreach (var token in old)
            token.InvalidatedAt = now;

        var codes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            // 10 haneli, okunabilir gruplu: "84213-90711" — elle yazması kolay
            var value = $"{RandomNumberGenerator.GetInt32(0, 100000):D5}-{RandomNumberGenerator.GetInt32(0, 100000):D5}";
            codes.Add(value);
            db.UserTokens.Add(new UserToken
            {
                UserId = userId,
                Purpose = UserTokenPurpose.TotpRecovery,
                TokenHash = UserTokens.Hash(value),
                ExpiresAt = now.AddYears(10), // kurtarma kodu süreyle değil kullanımla ölür
            });
        }

        return codes;
    }
}

public sealed record DisableTotpCommand(string Code) : ICommand<AcknowledgedResponse>;

public sealed class DisableTotpHandler(
    TenancyDbContext db, UserContext user, ICredentialProtector protector, IClock clock)
    : ICommandHandler<DisableTotpCommand, AcknowledgedResponse>
{
    public async Task<AcknowledgedResponse> Handle(DisableTotpCommand command, CancellationToken ct)
    {
        var userId = user.UserId ?? throw new PoyraException(401, "auth.no_user", "Oturum yok.");
        var record = await db.Users.SingleAsync(u => u.Id == userId, ct);

        if (!record.TotpEnabled || record.TotpSecretProtected is null)
            throw new PoyraException(409, "totp.not_enabled", "İki adımlı doğrulama zaten kapalı.");

        var code = command.Code.Trim();
        var secret = protector.Unprotect(record.TotpSecretProtected)["totp"];
        var valid = Totp.Verify(secret, code, clock.UtcNow)
                    || await TryConsumeRecoveryCodeAsync(db, userId, code, clock.UtcNow, ct);
        if (!valid)
            throw new PoyraException(400, "totp.invalid_code",
                "Kod doğrulanamadı — 6 haneli uygulama kodu ya da bir kurtarma kodu girin.");

        record.TotpSecretProtected = null;
        record.TotpPendingSecretProtected = null;
        record.TotpEnabledAt = null;

        // Kurtarma kodları ve hatırlanan cihazlar sırla birlikte ölür
        var tokens = await db.UserTokens
            .Where(t => t.UserId == userId
                        && (t.Purpose == UserTokenPurpose.TotpRecovery
                            || t.Purpose == UserTokenPurpose.TotpDevice)
                        && t.UsedAt == null && t.InvalidatedAt == null)
            .ToListAsync(ct);
        foreach (var token in tokens)
            token.InvalidatedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);
        return new AcknowledgedResponse(true, "İki adımlı doğrulama kapatıldı.");
    }

    internal static async Task<bool> TryConsumeRecoveryCodeAsync(
        TenancyDbContext db, Guid userId, string code, DateTimeOffset now, CancellationToken ct)
    {
        var hash = UserTokens.Hash(code);
        var token = await db.UserTokens.SingleOrDefaultAsync(
            t => t.UserId == userId && t.Purpose == UserTokenPurpose.TotpRecovery
                 && t.TokenHash == hash, ct);
        if (token is null || !token.IsUsable(now))
            return false;

        token.UsedAt = now; // silinmez, damgalanır
        return true;
    }
}

public sealed record PendingTotpEnrollmentQuery : IQuery<TotpEnrollmentDto?>;

public sealed class PendingTotpEnrollmentHandler(
    TenancyDbContext db, UserContext user, ICredentialProtector protector)
    : IQueryHandler<PendingTotpEnrollmentQuery, TotpEnrollmentDto?>
{
    public async Task<TotpEnrollmentDto?> Handle(PendingTotpEnrollmentQuery query, CancellationToken ct)
    {
        var userId = user.UserId ?? throw new PoyraException(401, "auth.no_user", "Oturum yok.");
        var record = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        if (record.TotpEnabled || record.TotpPendingSecretProtected is null)
            return null;

        var secret = protector.Unprotect(record.TotpPendingSecretProtected)["totp"];
        return new TotpEnrollmentDto(secret, Totp.BuildOtpauthUri(record.Email, secret));
    }
}


public sealed record TenantRequiresTotpQuery(Guid TenantId) : IQuery<bool>;

public sealed class TenantRequiresTotpHandler(TenancyDbContext db)
    : IQueryHandler<TenantRequiresTotpQuery, bool>
{
    public async Task<bool> Handle(TenantRequiresTotpQuery query, CancellationToken ct)
        => await db.Tenants.AsNoTracking()
            .AnyAsync(t => t.Id == query.TenantId && t.RequireTotpForPrivileged, ct);
}

public sealed record SetTotpRequirementCommand(bool Required) : ICommand<AcknowledgedResponse>;

public sealed class SetTotpRequirementHandler(TenancyDbContext db, TenantContext tenant)
    : ICommandHandler<SetTotpRequirementCommand, AcknowledgedResponse>
{
    public async Task<AcknowledgedResponse> Handle(SetTotpRequirementCommand command, CancellationToken ct)
    {
        var record = await db.Tenants.SingleAsync(t => t.Id == tenant.TenantId, ct);
        record.RequireTotpForPrivileged = command.Required;
        await db.SaveChangesAsync(ct);
        return new AcknowledgedResponse(true,
            command.Required ? "Owner ve admin için 2FA zorunlu." : "2FA zorunluluğu kaldırıldı.");
    }
}

public sealed record UserTotpEnabledQuery(Guid UserId) : IQuery<bool>;

public sealed class UserTotpEnabledHandler(TenancyDbContext db)
    : IQueryHandler<UserTotpEnabledQuery, bool>
{
    public async Task<bool> Handle(UserTotpEnabledQuery query, CancellationToken ct)
        => await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id == query.UserId && u.TotpEnabledAt != null, ct);
}

public sealed record VerifyTotpCommand(Guid UserId, string Code) : ICommand<bool>;

public sealed class VerifyTotpHandler(
    TenancyDbContext db, ICredentialProtector protector, IClock clock)
    : ICommandHandler<VerifyTotpCommand, bool>
{
    public async Task<bool> Handle(VerifyTotpCommand command, CancellationToken ct)
    {
        var record = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == command.UserId, ct);
        if (record is not { TotpSecretProtected: not null })
            return false;

        var code = command.Code.Trim();
        var secret = protector.Unprotect(record.TotpSecretProtected)["totp"];
        if (Totp.Verify(secret, code, clock.UtcNow))
            return true;

        if (await DisableTotpHandler.TryConsumeRecoveryCodeAsync(
                db, command.UserId, code, clock.UtcNow, ct))
        {
            await db.SaveChangesAsync(ct); // kurtarma kodunun kullanım damgası kalıcı olsun
            return true;
        }

        return false;
    }
}

public sealed record IssueTotpDeviceCommand(Guid UserId) : ICommand<string>;

public sealed class IssueTotpDeviceHandler(TenancyDbContext db, IClock clock)
    : ICommandHandler<IssueTotpDeviceCommand, string>
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public async Task<string> Handle(IssueTotpDeviceCommand command, CancellationToken ct)
    {
        var token = "ptd_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        db.UserTokens.Add(new UserToken
        {
            UserId = command.UserId,
            Purpose = UserTokenPurpose.TotpDevice,
            TokenHash = UserTokens.Hash(token),
            ExpiresAt = clock.UtcNow.Add(Lifetime),
        });
        await db.SaveChangesAsync(ct);
        return token;
    }
}

public sealed record CheckTotpDeviceQuery(Guid UserId, string Token) : IQuery<bool>;

public sealed class CheckTotpDeviceHandler(TenancyDbContext db, IClock clock)
    : IQueryHandler<CheckTotpDeviceQuery, bool>
{
    public async Task<bool> Handle(CheckTotpDeviceQuery query, CancellationToken ct)
    {
        var hash = UserTokens.Hash(query.Token);
        var now = clock.UtcNow;
        return await db.UserTokens.AsNoTracking().AnyAsync(
            t => t.UserId == query.UserId && t.Purpose == UserTokenPurpose.TotpDevice
                 && t.TokenHash == hash && t.UsedAt == null && t.InvalidatedAt == null
                 && t.ExpiresAt > now, ct);
    }
}
