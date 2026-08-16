using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Poyra.Modules.PaymentLinks.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.PaymentLinks.Features;

public sealed record PaymentLinkDto(
    string Id,
    string Slug,
    string Url,
    long? AmountMinor,
    string Currency,
    string Description,
    int MaxInstallments,
    DateTimeOffset? ExpiresAt,
    int MaxUsage,
    int SuccessCount,
    string Status,
    DateTimeOffset CreatedAt);

internal static class PaymentLinkMap
{
    public static PaymentLinkDto ToDto(PaymentLink link, string checkoutBaseUrl)
        => new(link.PublicId, link.Slug, $"{checkoutBaseUrl.TrimEnd('/')}/l/{link.Slug}",
            link.AmountMinor, link.Currency, link.Description, link.MaxInstallments,
            link.ExpiresAt, link.MaxUsage, link.SuccessCount,
            PaymentLinkStatusMap.ToDb[link.Status], link.CreatedAt);
}

public sealed record CreatePaymentLinkRequest(
    string Description,
    long? AmountMinor = null,
    string Currency = "TRY",
    int MaxInstallments = 1,
    DateTimeOffset? ExpiresAt = null,
    int MaxUsage = 0);

public sealed record CreatePaymentLinkCommand(
    string Description, long? AmountMinor, string Currency,
    int MaxInstallments, DateTimeOffset? ExpiresAt, int MaxUsage)
    : Poyra.SharedKernel.Cqrs.ICommand<PaymentLinkDto>;

public sealed class CreatePaymentLinkValidator : AbstractValidator<CreatePaymentLinkCommand>
{
    public CreatePaymentLinkValidator()
    {
        RuleFor(x => x.Description).NotEmpty().MaximumLength(300);
        RuleFor(x => x.AmountMinor).GreaterThan(0).When(x => x.AmountMinor.HasValue)
            .WithMessage("Tutar kuruş cinsinden ve 0'dan büyük olmalı (açık tutar için boş bırakın).");
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.MaxInstallments).InclusiveBetween(1, 12);
        RuleFor(x => x.MaxUsage).GreaterThanOrEqualTo(0);
    }
}

public sealed class CreatePaymentLinkHandler(
    PaymentLinksDbContext db, TenantContext tenant, IConfiguration configuration)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreatePaymentLinkCommand, PaymentLinkDto>
{
    public async Task<PaymentLinkDto> Handle(CreatePaymentLinkCommand command, CancellationToken ct)
    {
        var link = new PaymentLink
        {
            TenantId = tenant.TenantId,
            Slug = PaymentLink.NewSlug(),
            AmountMinor = command.AmountMinor,
            Currency = command.Currency.ToUpperInvariant(),
            Description = command.Description,
            MaxInstallments = command.MaxInstallments,
            ExpiresAt = command.ExpiresAt,
            MaxUsage = command.MaxUsage,
        };

        db.PaymentLinks.Add(link);
        db.PaymentLinkLookups.Add(new PaymentLinkLookup
        {
            Slug = link.Slug,
            TenantId = link.TenantId,
            PaymentLinkId = link.Id,
        });

        await db.SaveChangesAsync(ct);
        return PaymentLinkMap.ToDto(link, CheckoutBaseUrl(configuration));
    }

    internal static string CheckoutBaseUrl(IConfiguration configuration)
        => configuration["Poyra:CheckoutBaseUrl"]
           ?? configuration["Poyra:PublicBaseUrl"]
           ?? "http://localhost:5095";
}

public sealed class CreatePaymentLinkEndpoint(IDispatcher dispatcher)
    : Endpoint<CreatePaymentLinkRequest, PaymentLinkDto>
{
    public override void Configure()
    {
        Post("/v1/payment-links");
        Description(x => x.WithTags("PaymentLinks"));
        Summary(s => s.Summary =
            "Ödeme bağlantısı oluşturur (SMS/e-posta/QR ile paylaşılır). Tutar boşsa müşteri girer.");
    }

    public override async Task HandleAsync(CreatePaymentLinkRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new CreatePaymentLinkCommand(
            req.Description, req.AmountMinor, req.Currency, req.MaxInstallments,
            req.ExpiresAt, req.MaxUsage), ct), ct);
}

public sealed record ListPaymentLinksQuery : IQuery<IReadOnlyList<PaymentLinkDto>>;

public sealed class ListPaymentLinksHandler(PaymentLinksDbContext db, IConfiguration configuration)
    : IQueryHandler<ListPaymentLinksQuery, IReadOnlyList<PaymentLinkDto>>
{
    public async Task<IReadOnlyList<PaymentLinkDto>> Handle(ListPaymentLinksQuery query, CancellationToken ct)
    {
        var baseUrl = CreatePaymentLinkHandler.CheckoutBaseUrl(configuration);
        var links = await db.PaymentLinks.AsNoTracking()
            .OrderByDescending(l => l.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        return links.Select(l => PaymentLinkMap.ToDto(l, baseUrl)).ToList();
    }
}

public sealed class ListPaymentLinksEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<PaymentLinkDto>>
{
    public override void Configure()
    {
        Get("/v1/payment-links");
        Description(x => x.WithTags("PaymentLinks"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListPaymentLinksQuery(), ct), ct);
}

public sealed record LinkQrQuery(string PublicId) : IQuery<string>;

public sealed class LinkQrHandler(PaymentLinksDbContext db, IConfiguration configuration)
    : IQueryHandler<LinkQrQuery, string>
{
    public async Task<string> Handle(LinkQrQuery query, CancellationToken ct)
    {
        var link = await db.PaymentLinks.AsNoTracking()
                       .SingleOrDefaultAsync(l => l.PublicId == query.PublicId, ct)
                   ?? throw PoyraException.NotFound("payment_link.not_found", "Ödeme bağlantısı bulunamadı.");

        var url = $"{CreatePaymentLinkHandler.CheckoutBaseUrl(configuration).TrimEnd('/')}/l/{link.Slug}";
        return Poyra.SharedKernel.Domain.QrCode.ToSvg(url);
    }
}

public sealed class LinkQrEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/v1/payment-links/{id}/qr.svg");
        Description(x => x.WithTags("PaymentLinks"));
        Summary(s => s.Summary = "Bağlantının QR kodunu SVG olarak döner (baskıya uygun).");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var svg = await dispatcher.Ask(new LinkQrQuery(Route<string>("id")!), ct);
        await Send.StringAsync(svg, contentType: "image/svg+xml; charset=utf-8", cancellation: ct);
    }
}

public sealed record KarekodSettingsDto(
    bool Configured, string? SchemeGuid, string? MerchantNo,
    string? CategoryCode, string? MerchantName, string? MerchantCity);

public sealed record GetKarekodSettingsQuery : IQuery<KarekodSettingsDto>;

public sealed class GetKarekodSettingsHandler(PaymentLinksDbContext db, TenantContext tenant)
    : IQueryHandler<GetKarekodSettingsQuery, KarekodSettingsDto>
{
    public async Task<KarekodSettingsDto> Handle(GetKarekodSettingsQuery query, CancellationToken ct)
    {
        var settings = await db.KarekodSettings.AsNoTracking().SingleOrDefaultAsync(ct)
                       ?? Domain.KarekodSettings.Default(tenant.TenantId);

        return new KarekodSettingsDto(settings.IsConfigured, settings.SchemeGuid, settings.MerchantNo,
            settings.CategoryCode, settings.MerchantName, settings.MerchantCity);
    }
}

public sealed class GetKarekodSettingsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<KarekodSettingsDto>
{
    public override void Configure()
    {
        Get("/v1/karekod/settings");
        Description(x => x.WithTags("PaymentLinks"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new GetKarekodSettingsQuery(), ct), ct);
}

public sealed record SaveKarekodSettingsRequest(
    string? SchemeGuid = null, string? MerchantNo = null, string? CategoryCode = null,
    string? MerchantName = null, string? MerchantCity = null);

public sealed record SaveKarekodSettingsCommand(
    string? SchemeGuid, string? MerchantNo, string? CategoryCode,
    string? MerchantName, string? MerchantCity)
    : Poyra.SharedKernel.Cqrs.ICommand<KarekodSettingsDto>;

public sealed class SaveKarekodSettingsValidator : AbstractValidator<SaveKarekodSettingsCommand>
{
    public SaveKarekodSettingsValidator()
    {
        RuleFor(x => x.SchemeGuid).MaximumLength(32);
        RuleFor(x => x.MerchantNo).MaximumLength(32);
        RuleFor(x => x.CategoryCode).Matches("^[0-9]{4}$")
            .When(x => !string.IsNullOrWhiteSpace(x.CategoryCode))
            .WithMessage("İşyeri kategori kodu (MCC) 4 haneli rakam olmalı.");
        RuleFor(x => x.MerchantName).MaximumLength(25); // EMVCo alan sınırı
        RuleFor(x => x.MerchantCity).MaximumLength(15);
    }
}

public sealed class SaveKarekodSettingsHandler(PaymentLinksDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SaveKarekodSettingsCommand, KarekodSettingsDto>
{
    public async Task<KarekodSettingsDto> Handle(SaveKarekodSettingsCommand command, CancellationToken ct)
    {
        var settings = await db.KarekodSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = Domain.KarekodSettings.Default(tenant.TenantId);
            db.KarekodSettings.Add(settings);
        }

        settings.SchemeGuid = Trim(command.SchemeGuid) ?? settings.SchemeGuid;
        settings.MerchantNo = Trim(command.MerchantNo) ?? settings.MerchantNo;
        settings.CategoryCode = Trim(command.CategoryCode) ?? settings.CategoryCode;
        settings.MerchantName = Trim(command.MerchantName) ?? settings.MerchantName;
        settings.MerchantCity = Trim(command.MerchantCity) ?? settings.MerchantCity;

        await db.SaveChangesAsync(ct);
        return new KarekodSettingsDto(settings.IsConfigured, settings.SchemeGuid, settings.MerchantNo,
            settings.CategoryCode, settings.MerchantName, settings.MerchantCity);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class SaveKarekodSettingsEndpoint(IDispatcher dispatcher)
    : Endpoint<SaveKarekodSettingsRequest, KarekodSettingsDto>
{
    public override void Configure()
    {
        Post("/v1/karekod/settings");
        Description(x => x.WithTags("PaymentLinks"));
        Summary(s => s.Summary =
            "TR Karekod tanımlayıcılarını ayarlar. Değerler işyerinin BANKASINDAN alınır; "
            + "Poyra yalnız standart yükü kurar.");
    }

    public override async Task HandleAsync(SaveKarekodSettingsRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new SaveKarekodSettingsCommand(
            req.SchemeGuid, req.MerchantNo, req.CategoryCode,
            req.MerchantName, req.MerchantCity), ct), ct);
}

public sealed record LinkKarekodQuery(string PublicId) : IQuery<string>;

public sealed class LinkKarekodHandler(PaymentLinksDbContext db, TenantContext tenant)
    : IQueryHandler<LinkKarekodQuery, string>
{
    public async Task<string> Handle(LinkKarekodQuery query, CancellationToken ct)
    {
        var link = await db.PaymentLinks.AsNoTracking()
                       .SingleOrDefaultAsync(l => l.PublicId == query.PublicId, ct)
                   ?? throw PoyraException.NotFound("payment_link.not_found", "Ödeme bağlantısı bulunamadı.");

        var settings = await db.KarekodSettings.AsNoTracking().SingleOrDefaultAsync(ct)
                       ?? Domain.KarekodSettings.Default(tenant.TenantId);

        if (!settings.IsConfigured)
            throw new PoyraException(409, "karekod.not_configured",
                "TR Karekod tanımlayıcıları eksik. Şema tanımlayıcısı ve üye işyeri numarası "
                + "bankanızdan alınıp Ayarlar'a girilmelidir — eksik ayarla üretilen karekod geçersizdir.");

        return KarekodBuilder.Build(
            settings,
            settings.MerchantName ?? link.Description,
            settings.MerchantCity ?? "ISTANBUL",
            link.AmountMinor,
            link.Slug);
    }
}

public sealed class LinkKarekodEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/v1/payment-links/{id}/karekod.svg");
        Description(x => x.WithTags("PaymentLinks"));
        Summary(s => s.Summary =
            "Bağlantının TR Karekod'unu SVG olarak döner. Sabit tutar → dinamik, açık tutar → statik.");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var payload = await dispatcher.Ask(new LinkKarekodQuery(Route<string>("id")!), ct);
        await Send.StringAsync(Poyra.SharedKernel.Domain.QrCode.ToSvg(payload),
            contentType: "image/svg+xml; charset=utf-8", cancellation: ct);
    }
}

public sealed class LinkKarekodPayloadEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<string>
{
    public override void Configure()
    {
        Get("/v1/payment-links/{id}/karekod");
        Description(x => x.WithTags("PaymentLinks"));
        Summary(s => s.Summary = "TR Karekod ham yükü (bankanın test setiyle doğrulamak için).");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.StringAsync(await dispatcher.Ask(new LinkKarekodQuery(Route<string>("id")!), ct),
            contentType: "text/plain; charset=utf-8", cancellation: ct);
}

public sealed record DisablePaymentLinkCommand(string PublicId)
    : Poyra.SharedKernel.Cqrs.ICommand<PaymentLinkDto>;

public sealed class DisablePaymentLinkHandler(PaymentLinksDbContext db, IConfiguration configuration)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<DisablePaymentLinkCommand, PaymentLinkDto>
{
    public async Task<PaymentLinkDto> Handle(DisablePaymentLinkCommand command, CancellationToken ct)
    {
        var link = await db.PaymentLinks.SingleOrDefaultAsync(l => l.PublicId == command.PublicId, ct)
            ?? throw PoyraException.NotFound("payment_link.not_found", "Ödeme bağlantısı bulunamadı.");

        link.Status = PaymentLinkStatus.Disabled;
        await db.SaveChangesAsync(ct);
        return PaymentLinkMap.ToDto(link, CreatePaymentLinkHandler.CheckoutBaseUrl(configuration));
    }
}

public sealed class DisablePaymentLinkEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<PaymentLinkDto>
{
    public override void Configure()
    {
        Post("/v1/payment-links/{id}/disable");
        Description(x => x.WithTags("PaymentLinks"));
        Summary(s => s.Summary = "Bağlantıyı kapatır (silinmez — geçmiş tahsilatlar izlenebilir kalır).");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new DisablePaymentLinkCommand(Route<string>("id")!), ct), ct);
}
