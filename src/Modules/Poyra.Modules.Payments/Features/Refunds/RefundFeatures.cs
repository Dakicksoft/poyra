using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Payments.Infrastructure;
using Poyra.Modules.Webhooks.Contracts;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Payments.Features.Refunds;

public sealed record RefundResponse(
    string Id, // ref_…
    string PaymentId, // pay_…
    long AmountMinor,
    string Currency,
    RefundStatus Status,
    PaymentErrorDto? Error,
    DateTimeOffset CreatedAt);

internal static class RefundMap
{
    public static RefundResponse ToResponse(Refund refund, string paymentPublicId)
        => new(refund.PublicId, paymentPublicId, refund.AmountMinor, refund.Currency, refund.Status,
            refund.ErrorUnifiedCode is null
                ? null
                : new PaymentErrorDto(refund.ErrorUnifiedCode, refund.ErrorRawCode, refund.ErrorRawMessage),
            refund.CreatedAt);
}

public sealed record CreateRefundRequest(string PaymentId, long? AmountMinor = null);

public sealed record CreateRefundCommand(string PaymentId, long? AmountMinor)
    : Poyra.SharedKernel.Cqrs.ICommand<RefundResponse>;

public sealed class CreateRefundValidator : AbstractValidator<CreateRefundCommand>
{
    public CreateRefundValidator()
    {
        RuleFor(x => x.PaymentId).NotEmpty();
        RuleFor(x => x.AmountMinor).GreaterThan(0).When(x => x.AmountMinor.HasValue);
    }
}

public sealed class CreateRefundHandler(PaymentsDbContext db, IConnectorGateway gateway, IOutboxNudger outbox)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreateRefundCommand, RefundResponse>
{
    public async Task<RefundResponse> Handle(CreateRefundCommand command, CancellationToken ct)
    {
        var intent = await db.PaymentIntents.SingleOrDefaultAsync(p => p.PublicId == command.PaymentId, ct)
            ?? throw PoyraException.NotFound("payment.not_found", "Ödeme bulunamadı.");

        if (intent.Status != PaymentStatus.Succeeded)
            throw new PoyraException(409, "refund.invalid_state", "Yalnız başarılı ödemeler iade edilebilir.");

        var attempt = await db.PaymentAttempts
            .SingleAsync(a => a.PaymentIntentId == intent.Id && a.Status == AttemptStatus.Captured, ct);

        var chargedTotal = attempt.AmountMinor;

        var alreadyRefunded = await db.Refunds
            .Where(r => r.PaymentIntentId == intent.Id && r.Status != RefundStatus.Failed)
            .SumAsync(r => (long?)r.AmountMinor, ct) ?? 0;

        var amount = command.AmountMinor ?? chargedTotal - alreadyRefunded;
        if (amount <= 0 || alreadyRefunded + amount > chargedTotal)
            throw new PoyraException(400, "refund.amount_exceeds_remaining",
                $"İade edilebilir kalan tutar: {chargedTotal - alreadyRefunded} kuruş.");

        var refund = Refund.Open(intent, attempt, amount);
        db.Refunds.Add(refund);
        db.PaymentEvents.Add(PaymentEvent.For(intent, "refund.created", "api",
            new { amount_minor = amount }));
        await db.SaveChangesAsync(ct); // ref_… üretilsin; banka çağrısı öncesi iz kalsın

        var resolved = await gateway.ResolveAsync(attempt.ConnectorAccountId, ct);
        var result = await resolved.Connector.RefundAsync(
            new ConnectorRefundRequest(attempt.PublicId, attempt.ConnectorTxnId, amount, intent.Currency),
            resolved.Credentials, ct);

        if (result.Success)
        {
            refund.MarkSucceeded(result.ConnectorTxnId);
            db.PaymentEvents.Add(PaymentEvent.For(intent, "refund.succeeded", "connector",
                new { refund = refund.PublicId, amount_minor = amount }));
            db.OutboxMessages.Add(OutboxMessage.For(intent.TenantId, KnownWebhookEvents.RefundSucceeded, new
            {
                refund_id = refund.PublicId,
                payment_id = intent.PublicId,
                amount_minor = amount,
                currency = intent.Currency,
            }));
        }
        else
        {
            refund.MarkFailed(result.UnifiedCode, result.RawCode, result.RawMessage);
            db.PaymentEvents.Add(PaymentEvent.For(intent, "refund.failed", "connector",
                new { refund = refund.PublicId, error = result.UnifiedCode }));
            db.OutboxMessages.Add(OutboxMessage.For(intent.TenantId, KnownWebhookEvents.RefundFailed, new
            {
                refund_id = refund.PublicId,
                payment_id = intent.PublicId,
                amount_minor = amount,
                currency = intent.Currency,
                error = result.UnifiedCode,
            }));
        }

        await db.SaveChangesAsync(ct);
        outbox.Nudge();
        return RefundMap.ToResponse(refund, intent.PublicId);
    }
}

public sealed class CreateRefundEndpoint(IDispatcher dispatcher)
    : Endpoint<CreateRefundRequest, RefundResponse>
{
    public override void Configure()
    {
        Post("/v1/refunds");
        Description(x => x.WithTags("Refunds"));
        Summary(s => s.Summary = "Kısmi/tam iade. Tutar verilmezse kalan tutarın tamamı iade edilir.");
    }

    public override async Task HandleAsync(CreateRefundRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new CreateRefundCommand(req.PaymentId, req.AmountMinor), ct), ct);
}

public sealed record GetRefundQuery(string PublicId) : IQuery<RefundResponse?>;

public sealed class GetRefundHandler(PaymentsDbContext db)
    : IQueryHandler<GetRefundQuery, RefundResponse?>
{
    public async Task<RefundResponse?> Handle(GetRefundQuery query, CancellationToken ct)
    {
        var row = await (
            from refund in db.Refunds.AsNoTracking()
            join intent in db.PaymentIntents.AsNoTracking() on refund.PaymentIntentId equals intent.Id
            where refund.PublicId == query.PublicId
            select new { refund, intent.PublicId }).SingleOrDefaultAsync(ct);

        return row is null ? null : RefundMap.ToResponse(row.refund, row.PublicId);
    }
}

public sealed class GetRefundEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<RefundResponse>
{
    public override void Configure()
    {
        Get("/v1/refunds/{id}");
        Description(x => x.WithTags("Refunds"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await dispatcher.Ask(new GetRefundQuery(Route<string>("id")!), ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(result, ct);
    }
}
