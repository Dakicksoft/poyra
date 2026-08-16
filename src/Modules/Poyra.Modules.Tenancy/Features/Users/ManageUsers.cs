using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Tenancy.Features.Users;

public sealed record TenantUserDto(
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    bool EmailVerified,
    bool IsSelf,
    DateTimeOffset JoinedAt);

public sealed record ListUsersQuery : IQuery<IReadOnlyList<TenantUserDto>>;

public sealed class ListUsersHandler(TenancyDbContext db, TenantContext tenant, UserContext user)
    : IQueryHandler<ListUsersQuery, IReadOnlyList<TenantUserDto>>
{
    public async Task<IReadOnlyList<TenantUserDto>> Handle(ListUsersQuery query, CancellationToken ct)
    {
        var rows = await (
            from membership in db.UserTenants.AsNoTracking()
            join account in db.Users.AsNoTracking() on membership.UserId equals account.Id
            where membership.TenantId == tenant.TenantId
            orderby membership.Role descending, account.Email
            select new
            {
                account.Id,
                account.Email,
                account.DisplayName,
                account.IsActive,
                account.EmailVerifiedAt,
                membership.Role,
                membership.CreatedAt,
            }).ToListAsync(ct);

        return rows
            .Select(r => new TenantUserDto(
                r.Id, r.Email, r.DisplayName, TenantRoleMap.ToDb[r.Role], r.IsActive,
                r.EmailVerifiedAt is not null, r.Id == user.UserId, r.CreatedAt))
            .ToList();
    }
}

public sealed class ListUsersEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<IReadOnlyList<TenantUserDto>>
{
    public override void Configure()
    {
        Get("/v1/users");
        Description(x => x.WithTags("Tenancy"));
        Summary(s => s.Summary = "İşyerinin kullanıcıları ve rolleri.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListUsersQuery(), ct), ct);
}

public sealed record ChangeUserRoleRequest(string Role);

public sealed record ChangeUserRoleCommand(Guid UserId, string Role)
    : Poyra.SharedKernel.Cqrs.ICommand<TenantUserDto>;

public sealed class ChangeUserRoleValidator : AbstractValidator<ChangeUserRoleCommand>
{
    public ChangeUserRoleValidator()
        => RuleFor(x => x.Role).Must(TenantRoleMap.FromDb.ContainsKey)
            .WithMessage($"Geçersiz rol. Geçerli roller: {string.Join(", ", TenantRoleMap.FromDb.Keys)}");
}

public sealed class ChangeUserRoleHandler(TenancyDbContext db, TenantContext tenant, UserContext actor)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ChangeUserRoleCommand, TenantUserDto>
{
    public async Task<TenantUserDto> Handle(ChangeUserRoleCommand command, CancellationToken ct)
    {
        var membership = await db.UserTenants.SingleOrDefaultAsync(
                m => m.UserId == command.UserId && m.TenantId == tenant.TenantId, ct)
            ?? throw PoyraException.NotFound("user.not_member", "Kullanıcı bu işyerinde üye değil.");

        var newRole = TenantRoleMap.FromDb[command.Role];

        if (command.UserId == actor.UserId && newRole < membership.Role)
            throw PoyraException.Conflict("user.cannot_demote_self",
                "Kendi rolünüzü düşüremezsiniz — işyerinde yetkili kalmayabilir. "
                + "Başka bir yönetici bu değişikliği yapmalı.");

        if (membership.Role == TenantRole.Owner && newRole < TenantRole.Owner)
        {
            var otherOwners = await db.UserTenants.CountAsync(
                m => m.TenantId == tenant.TenantId
                     && m.Role == TenantRole.Owner
                     && m.UserId != command.UserId, ct);

            if (otherOwners == 0)
                throw PoyraException.Conflict("user.last_owner",
                    "Son sahibin rolü düşürülemez — önce başka bir sahip atayın.");
        }

        membership.Role = newRole;
        await db.SaveChangesAsync(ct);

        var account = await db.Users.AsNoTracking().SingleAsync(u => u.Id == command.UserId, ct);
        return new TenantUserDto(account.Id, account.Email, account.DisplayName, command.Role,
            account.IsActive, account.EmailVerifiedAt is not null,
            account.Id == actor.UserId, membership.CreatedAt);
    }
}

public sealed class ChangeUserRoleEndpoint(IDispatcher dispatcher) : Endpoint<ChangeUserRoleRequest, TenantUserDto>
{
    public override void Configure()
    {
        Post("/v1/users/{id}/role");
        Description(x => x.WithTags("Tenancy"));
        Summary(s => s.Summary = "Kullanıcının bu işyerindeki rolünü değiştirir.");
    }

    public override async Task HandleAsync(ChangeUserRoleRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new ChangeUserRoleCommand(Route<Guid>("id"), req.Role), ct), ct);
}

public sealed record RemoveUserCommand(Guid UserId) : Poyra.SharedKernel.Cqrs.ICommand<bool>;

public sealed class RemoveUserHandler(TenancyDbContext db, TenantContext tenant, UserContext actor, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<RemoveUserCommand, bool>
{
    public async Task<bool> Handle(RemoveUserCommand command, CancellationToken ct)
    {
        if (command.UserId == actor.UserId)
            throw PoyraException.Conflict("user.cannot_remove_self",
                "Kendi erişiminizi kaldıramazsınız.");

        var membership = await db.UserTenants.SingleOrDefaultAsync(
                m => m.UserId == command.UserId && m.TenantId == tenant.TenantId, ct)
            ?? throw PoyraException.NotFound("user.not_member", "Kullanıcı bu işyerinde üye değil.");

        if (membership.Role == TenantRole.Owner)
        {
            var otherOwners = await db.UserTenants.CountAsync(
                m => m.TenantId == tenant.TenantId
                     && m.Role == TenantRole.Owner
                     && m.UserId != command.UserId, ct);

            if (otherOwners == 0)
                throw PoyraException.Conflict("user.last_owner",
                    "Son sahip kaldırılamaz — önce başka bir sahip atayın.");
        }

        db.UserTenants.Remove(membership);

        var sessions = await db.RefreshTokens
            .Where(r => r.UserId == command.UserId && r.TenantId == tenant.TenantId && r.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var session in sessions)
            session.RevokedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed record RemoveUserResponse(bool Removed);

public sealed class RemoveUserEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<RemoveUserResponse>
{
    public override void Configure()
    {
        Delete("/v1/users/{id}");
        Description(x => x.WithTags("Tenancy"));
        Summary(s => s.Summary =
            "Kullanıcının bu işyerindeki erişimini kaldırır ve açık oturumlarını kapatır.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(new RemoveUserResponse(
            await dispatcher.Send(new RemoveUserCommand(Route<Guid>("id")), ct)), ct);
}
