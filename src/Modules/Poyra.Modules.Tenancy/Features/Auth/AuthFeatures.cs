using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Tenancy.Domain;
using Poyra.Modules.Tenancy.Infrastructure;
using Poyra.Modules.Tenancy.Security;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;
using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Tenancy.Features.Auth;

public sealed record TenantSummaryDto(Guid Id, string Slug, string Name);

public sealed record AuthTokensResponse(
    string AccessToken,
    int ExpiresInSeconds,
    string RefreshToken,
    TenantSummaryDto Tenant,
    string Role,
    Guid UserId);

public sealed record LoginRequest(string Email, string Password, string? TenantSlug = null);

public sealed record LoginCommand(string Email, string Password, string? TenantSlug)
    : Poyra.SharedKernel.Cqrs.ICommand<AuthTokensResponse>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class LoginHandler(
    TenancyDbContext db,
    IPasswordHasher<User> passwordHasher,
    JwtTokens jwt,
    IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<LoginCommand, AuthTokensResponse>
{
    public async Task<AuthTokensResponse> Handle(LoginCommand command, CancellationToken ct)
    {
        var email = TurkishText.NormalizeEmail(command.Email);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email && u.IsActive, ct);

        if (user is null || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, command.Password)
                == PasswordVerificationResult.Failed)
            throw PoyraException.Unauthorized("auth.invalid_credentials", "E-posta veya parola hatalı.");

        var memberships = await (
            from membership in db.UserTenants.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on membership.TenantId equals tenant.Id
            where membership.UserId == user.Id && tenant.Status == TenantStatus.Active
            select new { membership.Role, Tenant = tenant }).ToListAsync(ct);

        if (memberships.Count == 0)
            throw PoyraException.Unauthorized("auth.no_membership", "Kullanıcının aktif işyeri üyeliği yok.");

        var selected = command.TenantSlug is { } slug
            ? memberships.SingleOrDefault(m => m.Tenant.Slug == slug)
              ?? throw PoyraException.Unauthorized("auth.tenant_mismatch", "Bu işyerinde üyelik yok.")
            : memberships.Count == 1
                ? memberships[0]
                : throw new PoyraException(409, "auth.tenant_required",
                    "Birden çok işyeri üyeliği var — tenantSlug verin: "
                    + string.Join(", ", memberships.Select(m => m.Tenant.Slug)));

        var refreshPlain = RefreshTokens.Generate();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TenantId = selected.Tenant.Id,
            TokenHash = RefreshTokens.Hash(refreshPlain),
            ExpiresAt = clock.UtcNow + RefreshTokens.Lifetime,
        });
        await db.SaveChangesAsync(ct);

        return new AuthTokensResponse(
            jwt.Issue(new JwtPrincipal(user.Id, user.Email, selected.Tenant.Id, selected.Role)),
            (int)JwtTokens.AccessTokenLifetime.TotalSeconds,
            refreshPlain,
            new TenantSummaryDto(selected.Tenant.Id, selected.Tenant.Slug, selected.Tenant.Name),
            TenantRoleMap.ToDb[selected.Role],
            user.Id);
    }
}

public sealed class LoginEndpoint(IDispatcher dispatcher) : Endpoint<LoginRequest, AuthTokensResponse>
{
    public override void Configure()
    {
        Post("/v1/auth/login");
        Description(x => x.WithTags("Auth"));
        Summary(s => s.Summary = "E-posta + parola ile girer; 10 dk'lık JWT + 30 günlük refresh token döner.");
    }

    public override async Task HandleAsync(LoginRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new LoginCommand(req.Email, req.Password, req.TenantSlug), ct), ct);
}

public sealed record RefreshRequest(string RefreshToken);

public sealed record RefreshCommand(string RefreshToken)
    : Poyra.SharedKernel.Cqrs.ICommand<AuthTokensResponse>;

public sealed class RefreshHandler(TenancyDbContext db, JwtTokens jwt, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<RefreshCommand, AuthTokensResponse>
{
    public async Task<AuthTokensResponse> Handle(RefreshCommand command, CancellationToken ct)
    {
        var hash = RefreshTokens.Hash(command.RefreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null || !token.IsUsable(clock.UtcNow))
            throw PoyraException.Unauthorized("auth.refresh_invalid", "Refresh token geçersiz veya süresi dolmuş.");

        var user = await db.Users.SingleAsync(u => u.Id == token.UserId && u.IsActive, ct);
        var membership = await db.UserTenants.AsNoTracking()
            .SingleOrDefaultAsync(m => m.UserId == user.Id && m.TenantId == token.TenantId, ct)
            ?? throw PoyraException.Unauthorized("auth.no_membership", "Üyelik kaldırılmış.");
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == token.TenantId, ct);

        var refreshPlain = RefreshTokens.Generate();
        var newHash = RefreshTokens.Hash(refreshPlain);
        token.RevokedAt = clock.UtcNow;
        token.ReplacedByHash = newHash;
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TenantId = token.TenantId,
            TokenHash = newHash,
            ExpiresAt = clock.UtcNow + RefreshTokens.Lifetime,
        });
        await db.SaveChangesAsync(ct);

        return new AuthTokensResponse(
            jwt.Issue(new JwtPrincipal(user.Id, user.Email, tenant.Id, membership.Role)),
            (int)JwtTokens.AccessTokenLifetime.TotalSeconds,
            refreshPlain,
            new TenantSummaryDto(tenant.Id, tenant.Slug, tenant.Name),
            TenantRoleMap.ToDb[membership.Role],
            user.Id);
    }
}

public sealed class RefreshEndpoint(IDispatcher dispatcher) : Endpoint<RefreshRequest, AuthTokensResponse>
{
    public override void Configure()
    {
        Post("/v1/auth/refresh");
        Description(x => x.WithTags("Auth"));
    }

    public override async Task HandleAsync(RefreshRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new RefreshCommand(req.RefreshToken), ct), ct);
}

public sealed record LogoutRequest(string RefreshToken);

public sealed record LogoutCommand(string RefreshToken) : Poyra.SharedKernel.Cqrs.ICommand<bool>;

public sealed class LogoutHandler(TenancyDbContext db, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<LogoutCommand, bool>
{
    public async Task<bool> Handle(LogoutCommand command, CancellationToken ct)
    {
        var hash = RefreshTokens.Hash(command.RefreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is { RevokedAt: null })
        {
            token.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }
}

public sealed record LogoutResponse(bool Ok);

public sealed class LogoutEndpoint(IDispatcher dispatcher) : Endpoint<LogoutRequest, LogoutResponse>
{
    public override void Configure()
    {
        Post("/v1/auth/logout");
        Description(x => x.WithTags("Auth"));
    }

    public override async Task HandleAsync(LogoutRequest req, CancellationToken ct)
    {
        await dispatcher.Send(new LogoutCommand(req.RefreshToken), ct);
        await Send.OkAsync(new LogoutResponse(true), ct);
    }
}

public sealed record MeResponse(
    Guid UserId, string Email, string DisplayName, Guid TenantId, string Role, bool EmailVerified);

public sealed record MeQuery : IQuery<MeResponse?>;

public sealed class MeHandler(TenancyDbContext db, TenantContext tenant, UserContext user)
    : IQueryHandler<MeQuery, MeResponse?>
{
    public async Task<MeResponse?> Handle(MeQuery query, CancellationToken ct)
    {
        if (user.UserId is not { } userId || user.Role is not { } role)
            return null;

        var record = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        return new MeResponse(record.Id, record.Email, record.DisplayName,
            tenant.TenantId, TenantRoleMap.ToDb[role], record.EmailVerifiedAt is not null);
    }
}

public sealed class MeEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<MeResponse>
{
    public override void Configure()
    {
        Get("/v1/auth/me");
        Description(x => x.WithTags("Auth"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await dispatcher.Ask(new MeQuery(), ct);
        if (result is null)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await HttpContext.Response.WriteAsJsonAsync(
                new { code = "auth.user_required", message = "Bu uç yalnız kullanıcı JWT'siyle çağrılır.", errors = (object?)null }, ct);
            return;
        }

        await Send.OkAsync(result, ct);
    }
}
