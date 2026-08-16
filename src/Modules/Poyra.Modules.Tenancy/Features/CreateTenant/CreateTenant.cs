using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Poyra.Modules.Tenancy.Domain;
using Poyra.Modules.Tenancy.Features.Auth;
using Poyra.Modules.Tenancy.Security;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Messaging;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;
using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Tenancy.Features.CreateTenant;

public sealed record CreateTenantRequest(
    string Name,
    string Slug,
    string? OwnerEmail = null,
    string? OwnerPassword = null,
    string? OwnerName = null);

public sealed record CreateTenantCommand(
    string Name, string Slug, string? OwnerEmail, string? OwnerPassword, string? OwnerName)
    : Poyra.SharedKernel.Cqrs.ICommand<CreateTenantResponse>;

public sealed record CreateTenantResponse(
    Guid TenantId,
    Guid OrganizationId,
    Guid ProfileId,
    string Slug,
    string ApiKey, // yalnız bu yanıtta bir kez görünür; DB'de sadece özeti durur
    Guid? OwnerUserId = null);

public sealed class CreateTenantValidator : AbstractValidator<CreateTenantCommand>
{
    public CreateTenantValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(200);
        RuleFor(x => x.Slug).NotEmpty().Matches("^[a-z0-9-]{2,64}$")
            .WithMessage("Slug yalnız küçük harf, rakam ve tire içerebilir (2-64 karakter).");
        RuleFor(x => x.OwnerEmail).EmailAddress().When(x => x.OwnerEmail is not null);
        RuleFor(x => x.OwnerPassword).NotEmpty().MinimumLength(10)
            .When(x => x.OwnerEmail is not null)
            .WithMessage("Sahip parolası en az 10 karakter olmalı.");
    }
}

public sealed class CreateTenantHandler(
    TenancyDbContext db,
    TenantContext tenant,
    IPasswordHasher<User> passwordHasher,
    IEmailQueue emailQueue,
    IClock clock,
    IConfiguration configuration)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreateTenantCommand, CreateTenantResponse>
{
    public async Task<CreateTenantResponse> Handle(CreateTenantCommand command, CancellationToken ct)
    {
        if (await db.Tenants.AnyAsync(t => t.Slug == command.Slug, ct))
            throw PoyraException.Conflict("tenant.slug_conflict", $"'{command.Slug}' slug'ı zaten kullanımda.");

        var organization = new Organization { Name = command.Name };
        var newTenant = new Tenant { OrganizationId = organization.Id, Name = command.Name, Slug = command.Slug };
        var profile = new BusinessProfile { TenantId = newTenant.Id, Name = "Varsayılan", IsDefault = true };

        var plainKey = ApiKeys.Generate();
        var apiKey = new ApiKey
        {
            TenantId = newTenant.Id,
            Name = "Varsayılan anahtar",
            PrefixHint = ApiKeys.PrefixHint(plainKey),
            KeyHash = ApiKeys.Hash(plainKey),
        };

        tenant.Set(newTenant.Id);

        db.AddRange(organization, newTenant, profile, apiKey);

        Guid? ownerUserId = null;
        if (command.OwnerEmail is { } rawEmail)
        {
            var email = TurkishText.NormalizeEmail(rawEmail);
            var owner = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
            if (owner is null)
            {
                owner = new User { Email = email, PasswordHash = "", DisplayName = command.OwnerName ?? command.Name };
                owner.PasswordHash = passwordHasher.HashPassword(owner, command.OwnerPassword!);
                db.Users.Add(owner);

                await SendVerificationHandler.IssueVerificationAsync(
                    db, emailQueue, clock, configuration, owner, ct);
            }

            db.UserTenants.Add(new UserTenant
            {
                UserId = owner.Id,
                TenantId = newTenant.Id,
                Role = TenantRole.Owner,
            });
            ownerUserId = owner.Id;
        }

        await db.SaveChangesAsync(ct);

        return new CreateTenantResponse(
            newTenant.Id, organization.Id, profile.Id, newTenant.Slug, plainKey, ownerUserId);
    }
}

public sealed class CreateTenantEndpoint(IDispatcher dispatcher)
    : Endpoint<CreateTenantRequest, CreateTenantResponse>
{
    public override void Configure()
    {
        Post("/v1/tenants");
        Description(x => x.WithTags("Tenancy"));
        Summary(s => s.Summary = "Yeni işyeri oluşturur; API anahtarını bir defalık döner.");
    }

    public override async Task HandleAsync(CreateTenantRequest req, CancellationToken ct)
    {
        var response = await dispatcher.Send(new CreateTenantCommand(
            req.Name, req.Slug, req.OwnerEmail, req.OwnerPassword, req.OwnerName), ct);
        await Send.OkAsync(response, ct);
    }
}
