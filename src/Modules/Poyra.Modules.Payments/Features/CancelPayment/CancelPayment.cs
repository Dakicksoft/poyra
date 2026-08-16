using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Payments.Infrastructure;
using Poyra.Modules.Webhooks.Contracts;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Payments.Features.CancelPayment;

public sealed record CancelPaymentCommand(string PaymentPublicId)
    : Poyra.SharedKernel.Cqrs.ICommand<PaymentResponse>;

public sealed class CancelPaymentHandler(
    PaymentsDbContext db, IConnectorGateway gateway, PaymentMapper mapper, IOutboxNudger outbox)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CancelPaymentCommand, PaymentResponse>
{
    public async Task<PaymentResponse> Handle(CancelPaymentCommand command, CancellationToken ct)
    {
        var intent = await db.PaymentIntents
                         .SingleOrDefaultAsync(p => p.PublicId == command.PaymentPublicId, ct)
                     ?? throw PoyraException.NotFound("payment.not_found", "Ödeme bulunamadı.");

        if (intent.Status != PaymentStatus.Succeeded)
            throw new PoyraException(409, "payment.invalid_state",
                "Yalnız başarılı ödemeler iptal edilebilir (void).");

        var attempt = await db.PaymentAttempts
            .SingleAsync(a => a.PaymentIntentId == intent.Id && a.Status == AttemptStatus.Captured, ct);

        var resolved = await gateway.ResolveAsync(attempt.ConnectorAccountId, ct);
        var result = await resolved.Connector.VoidAsync(
            new ConnectorReference(attempt.PublicId, attempt.ConnectorTxnId), resolved.Credentials, ct);

        if (!result.Success)
        {
            db.PaymentEvents.Add(PaymentEvent.For(intent, "payment.void_failed", "connector", new
            {
                error = result.UnifiedCode,
                raw_code = result.RawCode,
            }));
            await db.SaveChangesAsync(ct);

            throw new PoyraException(409,
                result.UnifiedCode ?? UnifiedErrors.ProcessingError,
                result.UnifiedCode == UnifiedErrors.VoidWindowClosed
                    ? "Gün sonu kapanmış — iptal yerine iade (refund) oluşturun."
                    : result.RawMessage ?? "İptal başarısız.");
        }

        attempt.MarkVoided();
        intent.MarkCancelled();
        db.PaymentEvents.Add(PaymentEvent.For(intent, "payment.cancelled", "api",
            new { attempt = attempt.PublicId, connector_txn = result.ConnectorTxnId }));
        db.OutboxMessages.Add(OutboxMessage.For(intent.TenantId, KnownWebhookEvents.PaymentCancelled, new
        {
            payment_id = intent.PublicId,
            amount_minor = intent.AmountMinor,
            currency = intent.Currency,
        }));
        await db.SaveChangesAsync(ct);
        outbox.Nudge();

        return PaymentPresenter.Present(mapper, intent, attempt);
    }
}

public sealed class CancelPaymentEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<PaymentResponse>
{
    public override void Configure()
    {
        Post("/v1/payments/{id}/cancel");
        Description(x => x.WithTags("Payments"));
        Summary(s => s.Summary = "Gün sonu öncesi iptal (void) — komisyon doğmaz. Gün sonu sonrası: iade kullanın.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new CancelPaymentCommand(Route<string>("id")!), ct), ct);
}
