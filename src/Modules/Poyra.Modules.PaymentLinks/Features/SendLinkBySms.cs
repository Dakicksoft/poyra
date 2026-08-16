using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Poyra.Modules.PaymentLinks.Domain;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Messaging;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.PaymentLinks.Features;

public sealed record SendLinkBySmsRequest(string Phone, string? Message = null);

public sealed record SendLinkBySmsCommand(string PublicId, string Phone, string? Message)
    : Poyra.SharedKernel.Cqrs.ICommand<SendLinkBySmsResponse>;

public sealed record SendLinkBySmsResponse(bool Queued, string Phone, int Segments, string Body);

public sealed class SendLinkBySmsValidator : AbstractValidator<SendLinkBySmsCommand>
{
    public SendLinkBySmsValidator()
    {
        RuleFor(x => x.Phone).NotEmpty()
            .Must(TurkishPhone.IsValid)
            .WithMessage("Geçerli bir TR cep numarası gerekli (sabit hatlara SMS gönderilemez).");
        RuleFor(x => x.Message).MaximumLength(300);
    }
}

public sealed class SendLinkBySmsHandler(
    PaymentLinksDbContext db,
    ISmsQueue sms,
    TenantContext tenant,
    ITenantBrandingSource branding,
    IConfiguration configuration)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SendLinkBySmsCommand, SendLinkBySmsResponse>
{
    public async Task<SendLinkBySmsResponse> Handle(SendLinkBySmsCommand command, CancellationToken ct)
    {
        var link = await db.PaymentLinks.AsNoTracking()
                       .SingleOrDefaultAsync(l => l.PublicId == command.PublicId, ct)
                   ?? throw PoyraException.NotFound("payment_link.not_found", "Ödeme bağlantısı bulunamadı.");

        if (link.Status != PaymentLinkStatus.Active)
            throw new PoyraException(409, "payment_link.disabled",
                "Kapalı bağlantı gönderilemez — müşteri tıklayınca hata sayfası görür.");

        var url = PaymentLinkMap.ToDto(link, CreatePaymentLinkHandler.CheckoutBaseUrl(configuration)).Url;

        var brand = await branding.GetAsync(tenant.TenantId, ct);
        var merchant = brand?.DisplayName is { Length: > 0 } name ? name : "";

        var body = BuildBody(merchant, link, url, command.Message);
        var phone = TurkishPhone.ToE164(command.Phone)!;

        await sms.EnqueueAsync(new SmsMessage(tenant.TenantId, phone, body, "payment_link"), ct);

        return new SendLinkBySmsResponse(true, phone, TurkishPhone.SegmentCount(body), body);
    }

    private static string BuildBody(string merchant, PaymentLink link, string url, string? custom)
    {
        if (custom is { Length: > 0 })
            return $"{custom} {url}";

        var prefix = merchant.Length > 0 ? $"{TurkishPhone.ToAscii(merchant)}: " : "";
        var amount = link.AmountMinor is { } minor
            ? $"{(minor / 100m).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} TL "
            : "";

        return $"{prefix}{amount}odeme baglantiniz: {url}";
    }
}

public sealed class SendLinkBySmsEndpoint(IDispatcher dispatcher)
    : Endpoint<SendLinkBySmsRequest, SendLinkBySmsResponse>
{
    public override void Configure()
    {
        Post("/v1/payment-links/{id}/sms");
        Description(x => x.WithTags("PaymentLinks"));
        Summary(s => s.Summary =
            "Ödeme bağlantısını SMS ile gönderir (kuyruğa alır). Yanıtta tüketilen "
            + "kredi sayısı döner — Türkçe karakter sınırı 160'tan 70'e düşürür.");
    }

    public override async Task HandleAsync(SendLinkBySmsRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new SendLinkBySmsCommand(Route<string>("id")!, req.Phone, req.Message), ct), ct);
}
