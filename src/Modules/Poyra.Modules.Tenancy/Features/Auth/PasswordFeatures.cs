using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Poyra.Modules.Tenancy.Domain;
using Poyra.Modules.Tenancy.Infrastructure;
using Poyra.Modules.Tenancy.Security;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Messaging;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;
using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Tenancy.Features.Auth;

public sealed record AcknowledgedResponse(bool Accepted, string Message);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ForgotPasswordCommand(string Email)
    : Poyra.SharedKernel.Cqrs.ICommand<AcknowledgedResponse>;

public sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator() => RuleFor(x => x.Email).NotEmpty().EmailAddress();
}

public sealed class ForgotPasswordHandler(
    TenancyDbContext db, IEmailQueue email, IClock clock, IConfiguration configuration)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ForgotPasswordCommand, AcknowledgedResponse>
{
    public async Task<AcknowledgedResponse> Handle(ForgotPasswordCommand command, CancellationToken ct)
    {
        var normalized = TurkishText.NormalizeEmail(command.Email);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalized && u.IsActive, ct);

        if (user is not null)
        {
            var (token, _) = await UserTokenIssuer.IssueAsync(
                db, clock, user.Id, UserTokenPurpose.PasswordReset, ct);

            var link = $"{PanelBaseUrl(configuration)}/parola-sifirla?belirteç={Uri.EscapeDataString(token)}";
            await email.EnqueueAsync(
                EmailTemplates.PasswordReset(user.Email, user.DisplayName, link, UserTokens.ResetLifetime), ct);
        }

        return new AcknowledgedResponse(true,
            "Adres kayıtlıysa parola sıfırlama bağlantısı gönderildi. Gelen kutunuzu kontrol edin.");
    }

    internal static string PanelBaseUrl(IConfiguration configuration)
        => (configuration["Poyra:PanelBaseUrl"] ?? "http://localhost:5090").TrimEnd('/');
}

public sealed class ForgotPasswordEndpoint(IDispatcher dispatcher)
    : Endpoint<ForgotPasswordRequest, AcknowledgedResponse>
{
    public override void Configure()
    {
        Post("/v1/auth/password/forgot");
        Description(x => x.WithTags("Auth"));
        Summary(s => s.Summary =
            "Parola sıfırlama bağlantısı ister. Adres kayıtlı olmasa da aynı yanıtı döner (hesap keşfine kapalı).");
    }

    public override async Task HandleAsync(ForgotPasswordRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new ForgotPasswordCommand(req.Email), ct), ct);
}

public sealed record ResetPasswordRequest(string Token, string NewPassword);

public sealed record ResetPasswordCommand(string Token, string NewPassword)
    : Poyra.SharedKernel.Cqrs.ICommand<AcknowledgedResponse>;

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(10)
            .WithMessage("Parola en az 10 karakter olmalı.");
    }
}

public sealed class ResetPasswordHandler(
    TenancyDbContext db,
    IPasswordHasher<User> passwordHasher,
    IEmailQueue email,
    IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ResetPasswordCommand, AcknowledgedResponse>
{
    public async Task<AcknowledgedResponse> Handle(ResetPasswordCommand command, CancellationToken ct)
    {
        var hash = UserTokens.Hash(command.Token);
        var token = await db.UserTokens.SingleOrDefaultAsync(
            t => t.TokenHash == hash && t.Purpose == UserTokenPurpose.PasswordReset, ct);

        if (token is null || !token.IsUsable(clock.UtcNow))
            throw new PoyraException(400, "auth.reset_token_invalid",
                "Bağlantı geçersiz ya da süresi dolmuş. Yeni bir sıfırlama bağlantısı isteyin.");

        var user = await db.Users.SingleAsync(u => u.Id == token.UserId, ct);

        user.PasswordHash = passwordHasher.HashPassword(user, command.NewPassword);
        token.UsedAt = clock.UtcNow;

        var sessions = await db.RefreshTokens
            .Where(r => r.UserId == user.Id && r.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var session in sessions)
            session.RevokedAt = clock.UtcNow;

        var others = await db.UserTokens
            .Where(t => t.UserId == user.Id && t.Id != token.Id
                        && t.Purpose == UserTokenPurpose.PasswordReset
                        && t.UsedAt == null && t.InvalidatedAt == null)
            .ToListAsync(ct);
        foreach (var other in others)
            other.InvalidatedAt = clock.UtcNow;

        await email.EnqueueAsync(EmailTemplates.PasswordChanged(user.Email, user.DisplayName), ct);
        await db.SaveChangesAsync(ct);

        return new AcknowledgedResponse(true,
            $"Parolanız değiştirildi ve açık {sessions.Count} oturum kapatıldı. Yeni parolanızla giriş yapın.");
    }
}

public sealed class ResetPasswordEndpoint(IDispatcher dispatcher)
    : Endpoint<ResetPasswordRequest, AcknowledgedResponse>
{
    public override void Configure()
    {
        Post("/v1/auth/password/reset");
        Description(x => x.WithTags("Auth"));
        Summary(s => s.Summary =
            "Belirteçle parolayı değiştirir; kullanıcının TÜM açık oturumlarını kapatır.");
    }

    public override async Task HandleAsync(ResetPasswordRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new ResetPasswordCommand(req.Token, req.NewPassword), ct), ct);
}

public sealed record SendVerificationCommand : Poyra.SharedKernel.Cqrs.ICommand<AcknowledgedResponse>;

public sealed class SendVerificationHandler(
    TenancyDbContext db, UserContext user, IEmailQueue email, IClock clock, IConfiguration configuration)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SendVerificationCommand, AcknowledgedResponse>
{
    public async Task<AcknowledgedResponse> Handle(SendVerificationCommand command, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
            throw PoyraException.Unauthorized("auth.user_required", "Bu işlem için kullanıcı oturumu gerekli.");

        var account = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw PoyraException.NotFound("user.not_found", "Kullanıcı bulunamadı.");

        if (account.EmailVerifiedAt is not null)
            return new AcknowledgedResponse(false, "Bu adres zaten doğrulanmış.");

        await IssueVerificationAsync(db, email, clock, configuration, account, ct);
        await db.SaveChangesAsync(ct);

        return new AcknowledgedResponse(true, $"Doğrulama bağlantısı {account.Email} adresine gönderildi.");
    }

    internal static async Task IssueVerificationAsync(
        TenancyDbContext db, IEmailQueue email, IClock clock,
        IConfiguration configuration, User account, CancellationToken ct)
    {
        var (token, _) = await UserTokenIssuer.IssueAsync(
            db, clock, account.Id, UserTokenPurpose.EmailVerification, ct);

        var link = $"{ForgotPasswordHandler.PanelBaseUrl(configuration)}"
                   + $"/eposta-dogrula?belirteç={Uri.EscapeDataString(token)}";

        await email.EnqueueAsync(
            EmailTemplates.EmailVerification(account.Email, account.DisplayName, link, UserTokens.VerifyLifetime), ct);
    }
}

public sealed class SendVerificationEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<AcknowledgedResponse>
{
    public override void Configure()
    {
        Post("/v1/auth/email/send-verification");
        Description(x => x.WithTags("Auth"));
        Summary(s => s.Summary = "Oturumdaki kullanıcıya yeni doğrulama bağlantısı gönderir.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new SendVerificationCommand(), ct), ct);
}

public sealed record VerifyEmailRequest(string Token);

public sealed record VerifyEmailCommand(string Token)
    : Poyra.SharedKernel.Cqrs.ICommand<AcknowledgedResponse>;

public sealed class VerifyEmailHandler(TenancyDbContext db, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<VerifyEmailCommand, AcknowledgedResponse>
{
    public async Task<AcknowledgedResponse> Handle(VerifyEmailCommand command, CancellationToken ct)
    {
        var hash = UserTokens.Hash(command.Token);
        var token = await db.UserTokens.SingleOrDefaultAsync(
            t => t.TokenHash == hash && t.Purpose == UserTokenPurpose.EmailVerification, ct);

        if (token is null || !token.IsUsable(clock.UtcNow))
            throw new PoyraException(400, "auth.verify_token_invalid",
                "Doğrulama bağlantısı geçersiz ya da süresi dolmuş. Panelden yenisini isteyin.");

        var user = await db.Users.SingleAsync(u => u.Id == token.UserId, ct);
        user.EmailVerifiedAt ??= clock.UtcNow; // ikinci kez doğrulama tarihi bozmaz
        token.UsedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);
        return new AcknowledgedResponse(true, $"{user.Email} adresi doğrulandı.");
    }
}

public sealed class VerifyEmailEndpoint(IDispatcher dispatcher)
    : Endpoint<VerifyEmailRequest, AcknowledgedResponse>
{
    public override void Configure()
    {
        Post("/v1/auth/email/verify");
        Description(x => x.WithTags("Auth"));
        Summary(s => s.Summary = "Doğrulama belirtecini tüketir ve adresi doğrulanmış işaretler.");
    }

    public override async Task HandleAsync(VerifyEmailRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new VerifyEmailCommand(req.Token), ct), ct);
}

internal static class UserTokenIssuer
{
    public static async Task<(string Plain, UserToken Token)> IssueAsync(
        TenancyDbContext db, IClock clock, Guid userId, UserTokenPurpose purpose, CancellationToken ct)
    {
        var pending = await db.UserTokens
            .Where(t => t.UserId == userId && t.Purpose == purpose
                        && t.UsedAt == null && t.InvalidatedAt == null)
            .ToListAsync(ct);
        foreach (var previous in pending)
            previous.InvalidatedAt = clock.UtcNow;

        var plain = UserTokens.Generate(purpose);
        var token = new UserToken
        {
            UserId = userId,
            Purpose = purpose,
            TokenHash = UserTokens.Hash(plain),
            ExpiresAt = clock.UtcNow + UserTokens.Lifetime(purpose),
        };

        db.UserTokens.Add(token);
        return (plain, token);
    }
}
