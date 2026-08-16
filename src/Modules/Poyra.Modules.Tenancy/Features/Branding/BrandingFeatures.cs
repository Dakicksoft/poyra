using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.Modules.Tenancy.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Tenancy.Features.Branding;

public sealed record BrandingDto(
    string? DisplayName,
    string PrimaryColor,
    bool HasLogo,
    string? SupportEmail,
    string? SupportPhone,
    string? CheckoutDomain);

internal static class BrandingMap
{
    public static BrandingDto ToDto(TenantBranding b)
        => new(b.DisplayName, b.EffectiveColor, b.LogoBytes is { Length: > 0 },
            b.SupportEmail, b.SupportPhone, b.CheckoutDomain);
}

public sealed record GetBrandingQuery : IQuery<BrandingDto>;

public sealed class GetBrandingHandler(TenancyDbContext db, TenantContext tenant)
    : IQueryHandler<GetBrandingQuery, BrandingDto>
{
    public async Task<BrandingDto> Handle(GetBrandingQuery query, CancellationToken ct)
        => BrandingMap.ToDto(
            await db.TenantBrandings.AsNoTracking().SingleOrDefaultAsync(ct)
            ?? new TenantBranding { TenantId = tenant.TenantId });
}

public sealed class GetBrandingEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<BrandingDto>
{
    public override void Configure()
    {
        Get("/v1/branding");
        Description(x => x.WithTags("Tenancy"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new GetBrandingQuery(), ct), ct);
}

/// <param name="LogoBase64">Boş bırakılırsa mevcut logo KORUNUR; "-" gönderilirse silinir.</param>
public sealed record SaveBrandingRequest(
    string? DisplayName = null,
    string? PrimaryColor = null,
    string? SupportEmail = null,
    string? SupportPhone = null,
    string? CheckoutDomain = null,
    string? LogoBase64 = null,
    string? LogoContentType = null);

public sealed record SaveBrandingCommand(
    string? DisplayName, string? PrimaryColor, string? SupportEmail, string? SupportPhone,
    string? CheckoutDomain, string? LogoBase64, string? LogoContentType)
    : Poyra.SharedKernel.Cqrs.ICommand<BrandingDto>;

public sealed class SaveBrandingValidator : AbstractValidator<SaveBrandingCommand>
{
    public const int MaxLogoBytes = 256 * 1024;

    private static readonly string[] AllowedTypes = ["image/png", "image/jpeg", "image/svg+xml", "image/webp"];

    public SaveBrandingValidator()
    {
        RuleFor(x => x.DisplayName).MaximumLength(120);
        RuleFor(x => x.PrimaryColor).Matches("^#[0-9A-Fa-f]{6}$")
            .When(x => !string.IsNullOrWhiteSpace(x.PrimaryColor))
            .WithMessage("Renk #RRGGBB biçiminde olmalı.");
        RuleFor(x => x.SupportEmail).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.SupportEmail));
        RuleFor(x => x.SupportPhone).MaximumLength(32);
        RuleFor(x => x.CheckoutDomain)
            .Matches("^(?i)[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")
            .When(x => !string.IsNullOrWhiteSpace(x.CheckoutDomain))
            .WithMessage("Alan adı geçerli değil (ör. odeme.magaza.com).");

        When(x => x.LogoBase64 is { Length: > 1 }, () =>
        {
            RuleFor(x => x.LogoContentType).Must(t => t is not null && AllowedTypes.Contains(t))
                .WithMessage("Logo türü png, jpeg, svg veya webp olmalı.");
            RuleFor(x => x.LogoBase64!).Must(BeWithinSizeLimit)
                .WithMessage($"Logo en çok {MaxLogoBytes / 1024} KB olabilir.");
        });
    }

    private static bool BeWithinSizeLimit(string base64)
        => Convert.TryFromBase64String(base64, new byte[MaxLogoBytes], out _);
}

public sealed class SaveBrandingHandler(TenancyDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SaveBrandingCommand, BrandingDto>
{
    public async Task<BrandingDto> Handle(SaveBrandingCommand command, CancellationToken ct)
    {
        var branding = await db.TenantBrandings.SingleOrDefaultAsync(ct);
        if (branding is null)
        {
            branding = new TenantBranding { TenantId = tenant.TenantId };
            db.TenantBrandings.Add(branding);
        }

        branding.DisplayName = Trim(command.DisplayName) ?? branding.DisplayName;
        branding.PrimaryColor = Trim(command.PrimaryColor) ?? branding.PrimaryColor;
        branding.SupportEmail = Trim(command.SupportEmail) ?? branding.SupportEmail;
        branding.SupportPhone = Trim(command.SupportPhone) ?? branding.SupportPhone;


        if (Trim(command.CheckoutDomain) is { } domain)
            branding.CheckoutDomain = TurkishText.Fold(domain);

        if (command.LogoBase64 == "-")
        {
            branding.LogoBytes = null;
            branding.LogoContentType = null;
        }
        else if (command.LogoBase64 is { Length: > 1 })
        {
            branding.LogoBytes = Convert.FromBase64String(command.LogoBase64);
            branding.LogoContentType = command.LogoContentType;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDomainConflict(ex))
        {
            throw PoyraException.Conflict("branding.domain_conflict",
                $"'{branding.CheckoutDomain}' alan adı başka bir işyerinde tanımlı.");
        }

        return BrandingMap.ToDto(branding);
    }

    private static bool IsDomainConflict(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
           && pg.ConstraintName?.Contains("checkout_domain", StringComparison.Ordinal) == true;

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class SaveBrandingEndpoint(IDispatcher dispatcher) : Endpoint<SaveBrandingRequest, BrandingDto>
{
    public override void Configure()
    {
        Post("/v1/branding");
        Description(x => x.WithTags("Tenancy"));
        Summary(s => s.Summary =
            "Checkout markasını ayarlar (ad, renk, logo, destek bilgisi, kendi alan adı).");
    }

    public override async Task HandleAsync(SaveBrandingRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new SaveBrandingCommand(
            req.DisplayName, req.PrimaryColor, req.SupportEmail, req.SupportPhone,
            req.CheckoutDomain, req.LogoBase64, req.LogoContentType), ct), ct);
}

public sealed class TenantBrandingSource(TenancyDbContext db, TenantContext tenant)
    : ITenantBrandingSource
{
    public const string LogoMarker = "logo";

    public async Task<CheckoutBranding> GetAsync(Guid tenantId, CancellationToken ct)
    {
        tenant.Set(tenantId);

        var branding = await db.TenantBrandings.AsNoTracking().SingleOrDefaultAsync(ct);
        var name = branding?.DisplayName;

        if (string.IsNullOrWhiteSpace(name))
        {
            name = await db.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => t.Name)
                .SingleOrDefaultAsync(ct) ?? "Ödeme";
        }

        var logoUrl = branding?.LogoBytes is { Length: > 0 } ? LogoMarker : null;

        return new CheckoutBranding(
            name!,
            branding?.EffectiveColor ?? "#C4713B",
            logoUrl,
            branding?.SupportEmail,
            branding?.SupportPhone);
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetLogoAsync(Guid tenantId, CancellationToken ct)
    {
        tenant.Set(tenantId);

        var branding = await db.TenantBrandings.AsNoTracking().SingleOrDefaultAsync(ct);
        return branding?.LogoBytes is { Length: > 0 } bytes
            ? (bytes, branding.LogoContentType ?? "application/octet-stream")
            : null;
    }
}
