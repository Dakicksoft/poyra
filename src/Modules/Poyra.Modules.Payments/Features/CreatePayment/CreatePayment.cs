using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Payments.Features.ConfirmPayment;
using Poyra.Modules.Routing.Contracts;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Payments.Features.CreatePayment;

public sealed record CreatePaymentRequest(
    long AmountMinor,
    string Currency = "TRY",
    string? Description = null,
    int Installments = 1,
    string? ReturnUrl = null,
    bool Confirm = false,
    string? Program = null,
    string? CustomerRef = null,
    string? CustomerIp = null);

/// <param name="Channel">
/// Ödemenin Poyra'ya girdiği yol (bkz. PaymentChannels). İSTEK GÖVDESİNDEN ALINMAZ —
/// çağıran uç noktanın kendisi bildirir: API ucu "api", checkout linkin kaynağını,
/// abonelik faturalayıcısı "subscription" yazar. İşyerinin beyanına bırakılsaydı rota
/// kuralı doğrulanamayan bir alana dayanırdı.
/// </param>
public sealed record CreatePaymentCommand(
    long AmountMinor, string Currency, string? Description, int Installments, string? ReturnUrl,
    string? CustomerRef = null, string? CustomerIp = null, string? Channel = null)
    : Poyra.SharedKernel.Cqrs.ICommand<PaymentResponse>;

public sealed class CreatePaymentValidator : AbstractValidator<CreatePaymentCommand>
{
    public CreatePaymentValidator()
    {
        RuleFor(x => x.AmountMinor).GreaterThan(0).WithMessage("Tutar kuruş cinsinden ve 0'dan büyük olmalı.");
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Installments).InclusiveBetween(1, 12);
        RuleFor(x => x.CustomerRef).MaximumLength(100);
        RuleFor(x => x.CustomerIp).MaximumLength(45);
        RuleFor(x => x.ReturnUrl)
            .Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri)
                       && uri.Scheme is "http" or "https")
            .When(x => x.ReturnUrl is not null)
            .WithMessage("return_url mutlak bir http(s) adresi olmalı.");
    }
}

public sealed class CreatePaymentHandler(PaymentsDbContext db, TenantContext tenant, PaymentMapper mapper)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreatePaymentCommand, PaymentResponse>
{
    public async Task<PaymentResponse> Handle(CreatePaymentCommand command, CancellationToken ct)
    {
        var intent = PaymentIntent.Create(
            tenant.TenantId,
            tenant.ProfileId,
            Money.Of(command.AmountMinor, command.Currency),
            command.Description,
            command.Installments,
            command.ReturnUrl,
            command.CustomerRef,
            command.CustomerIp,
            command.Channel);

        db.PaymentIntents.Add(intent);
        db.PaymentEvents.Add(PaymentEvent.For(intent, "payment.created", actor: "api",
            payload: new { amount_minor = intent.AmountMinor, currency = intent.Currency }));

        await db.SaveChangesAsync(ct);

        return mapper.ToResponse(intent);
    }
}

public sealed class CreatePaymentEndpoint(IDispatcher dispatcher)
    : Endpoint<CreatePaymentRequest, PaymentResponse>
{
    public override void Configure()
    {
        Post("/v1/payments");
        Description(x => x.WithTags("Payments"));
        Summary(s => s.Summary = "Ödeme niyeti oluşturur; confirm=true ile aynı çağrıda rotaya sokar.");
    }

    public override async Task HandleAsync(CreatePaymentRequest req, CancellationToken ct)
    {
        // IP işyeri gönderirse ona güvenilir; göndermezse isteğin geldiği adres kullanılır.
        // İşyeri sunucusu araya girdiği için bu genelde İŞYERİNİN IP'sidir — bu yüzden
        // gerçek müşteri IP'sini customerIp ile iletmek işyerinin sorumluluğundadır.
        var ip = req.CustomerIp ?? HttpContext.Connection.RemoteIpAddress?.ToString();

        var created = await dispatcher.Send(new CreatePaymentCommand(
            req.AmountMinor, req.Currency, req.Description, req.Installments, req.ReturnUrl,
            req.CustomerRef, ip, PaymentChannels.Api), ct);

        var response = req.Confirm
            ? await dispatcher.Send(new ConfirmPaymentCommand(created.Id, null, req.Program), ct)
            : created;

        await Send.OkAsync(response, ct);
    }
}
