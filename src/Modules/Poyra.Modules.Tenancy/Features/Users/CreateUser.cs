using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Tenancy.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Tenancy.Features.Users;

public sealed record CreateUserRequest(string Email, string Password, string DisplayName, string Role);

public sealed record CreateUserCommand(string Email, string Password, string DisplayName, string Role)
    : Poyra.SharedKernel.Cqrs.ICommand<CreateUserResponse>;

public sealed record CreateUserResponse(Guid UserId, string Email, string DisplayName, string Role);

public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(10)
            .WithMessage("Parola en az 10 karakter olmalı.");
        RuleFor(x => x.DisplayName).NotEmpty().MinimumLength(2).MaximumLength(200);
        RuleFor(x => x.Role).Must(r => TenantRoleMap.FromDb.ContainsKey(r))
            .WithMessage($"Geçersiz rol. Geçerli roller: {string.Join(", ", TenantRoleMap.FromDb.Keys)}");
    }
}

public sealed class CreateUserHandler(
    TenancyDbContext db, TenantContext tenant, IPasswordHasher<User> passwordHasher)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreateUserCommand, CreateUserResponse>
{
    public async Task<CreateUserResponse> Handle(CreateUserCommand command, CancellationToken ct)
    {
        var email = TurkishText.NormalizeEmail(command.Email);
        var role = TenantRoleMap.FromDb[command.Role];

        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            user = new User { Email = email, PasswordHash = "", DisplayName = command.DisplayName };
            user.PasswordHash = passwordHasher.HashPassword(user, command.Password);
            db.Users.Add(user);
        }

        if (await db.UserTenants.AnyAsync(m => m.UserId == user.Id && m.TenantId == tenant.TenantId, ct))
            throw PoyraException.Conflict("user.already_member", "Kullanıcı bu işyerinde zaten üye.");

        db.UserTenants.Add(new UserTenant { UserId = user.Id, TenantId = tenant.TenantId, Role = role });
        await db.SaveChangesAsync(ct);

        return new CreateUserResponse(user.Id, user.Email, user.DisplayName, command.Role);
    }
}

public sealed class CreateUserEndpoint(IDispatcher dispatcher)
    : Endpoint<CreateUserRequest, CreateUserResponse>
{
    public override void Configure()
    {
        Post("/v1/users");
        Description(x => x.WithTags("Tenancy"));
        Summary(s => s.Summary = "İşyerine kullanıcı ekler (Admin+). Var olan e-postaya yalnız üyelik verilir.");
    }

    public override async Task HandleAsync(CreateUserRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new CreateUserCommand(req.Email, req.Password, req.DisplayName, req.Role), ct), ct);
}
