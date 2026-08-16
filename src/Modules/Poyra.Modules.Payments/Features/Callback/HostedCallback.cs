using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Payments.Infrastructure;
using Poyra.Modules.Webhooks.Contracts;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Notifications;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Payments.Features.Callback;

public sealed record CallbackOutcome(string PaymentId, PaymentStatus Status, string? ReturnUrl);

public sealed record CompleteHostedPaymentCommand(string Token, Dictionary<string, string> Form)
    : Poyra.SharedKernel.Cqrs.ICommand<CallbackOutcome>;

public sealed class CompleteHostedPaymentHandler(
    PaymentsDbContext db,
    IConnectorGateway gateway,
    IClock clock,
    IOutboxNudger outbox,
    INotificationPublisher notifications)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CompleteHostedPaymentCommand, CallbackOutcome>
{
    public async Task<CallbackOutcome> Handle(CompleteHostedPaymentCommand command, CancellationToken ct)
    {
        var token = await db.CallbackTokens.SingleOrDefaultAsync(t => t.Token == command.Token, ct)
            ?? throw PoyraException.NotFound("callback.token_unknown", "Geçersiz callback adresi.");

        var attempt = await db.PaymentAttempts.SingleAsync(a => a.Id == token.PaymentAttemptId, ct);
        var intent = await db.PaymentIntents.SingleAsync(p => p.Id == token.PaymentIntentId, ct);

        if (token.UsedAt is not null)
            return new CallbackOutcome(intent.PublicId, intent.Status, intent.ReturnUrl); // banka çift POST'u

        if (token.ExpiresAt < clock.UtcNow)
            throw new PoyraException(410, "callback.token_expired", "Callback süresi dolmuş.");

        var resolved = await gateway.ResolveAsync(attempt.ConnectorAccountId, ct);

        var form = MergeConnectorState(command.Form, attempt.ConnectorStateJson);

        var result = await resolved.Connector.CompleteHostedCallbackAsync(form, resolved.Credentials, ct);

        if (result.Success && !string.Equals(result.OrderId, attempt.PublicId, StringComparison.Ordinal))
            throw new PoyraException(400, "callback.order_mismatch", "Callback sipariş numarası eşleşmiyor.");

        token.UsedAt = clock.UtcNow;

        if (result.Success)
        {
            attempt.MarkCaptured(result.AuthCode, result.ConnectorTxnId, result.MaskedPan, clock.UtcNow);
            intent.MarkSucceeded();
            db.PaymentEvents.Add(PaymentEvent.For(intent, "payment.succeeded", "connector", new
            {
                attempt = attempt.PublicId,
                auth_code = result.AuthCode,
                masked_pan = result.MaskedPan,
            }));
            db.OutboxMessages.Add(OutboxMessage.For(intent.TenantId, KnownWebhookEvents.PaymentSucceeded, new
            {
                payment_id = intent.PublicId,
                amount_minor = intent.AmountMinor,
                currency = intent.Currency,
                installments = intent.Installments,
                masked_pan = result.MaskedPan,
            }));
        }
        else
        {
            attempt.MarkFailed(result.UnifiedCode, result.RawCode, result.RawMessage);
            intent.MarkFailed();
            db.PaymentEvents.Add(PaymentEvent.For(intent, "payment.failed", "connector", new
            {
                attempt = attempt.PublicId,
                error = result.UnifiedCode,
                raw_code = result.RawCode,
            }));
            db.OutboxMessages.Add(OutboxMessage.For(intent.TenantId, KnownWebhookEvents.PaymentFailed, new
            {
                payment_id = intent.PublicId,
                amount_minor = intent.AmountMinor,
                currency = intent.Currency,
                error = result.UnifiedCode,
            }));
        }

        await db.SaveChangesAsync(ct);
        outbox.Nudge();

        await notifications.PublishAsync(new PoyraNotification(
            intent.TenantId,
            result.Success ? PoyraNotificationTypes.PaymentSucceeded : PoyraNotificationTypes.PaymentFailed,
            intent.PublicId), ct);

        return new CallbackOutcome(intent.PublicId, intent.Status, intent.ReturnUrl);
    }

    private static IReadOnlyDictionary<string, string> MergeConnectorState(
        Dictionary<string, string> form, string? connectorStateJson)
    {
        if (connectorStateJson is not { Length: > 0 })
            return form;

        var state = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string>>(connectorStateJson);
        if (state is null)
            return form;

        var merged = new Dictionary<string, string>(state, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in form)
            merged[key] = value; // bankanın dediği kazanır

        return merged;
    }
}

public sealed record CallbackHttpResponse(string PaymentId, string Status);


public sealed class HostedCallbackEndpoint(IDispatcher dispatcher, PaymentsDbContext db, TenantContext tenant)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/v1/callbacks/{connectorKey}/{token}");
        Description(x => x.WithTags("Payments"));
        Summary(s => s.Summary = "Banka 3DS dönüş ucu (form POST). Kimlik: tek kullanımlık belirteç.");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var tokenValue = Route<string>("token")!;

        var token = await db.CallbackTokens.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Token == tokenValue, ct);
        if (token is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        tenant.Set(token.TenantId);

        var form = HttpContext.Request.HasFormContentType
            ? (await HttpContext.Request.ReadFormAsync(ct))
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToString())
            : new Dictionary<string, string>();
        foreach (var (key, value) in HttpContext.Request.Query)
            form.TryAdd(key, value.ToString());

        var outcome = await dispatcher.Send(new CompleteHostedPaymentCommand(tokenValue, form), ct);

        if (outcome.ReturnUrl is not null)
        {
            var separator = outcome.ReturnUrl.Contains('?') ? '&' : '?';
            await Send.RedirectAsync(
                $"{outcome.ReturnUrl}{separator}poyra_payment_id={outcome.PaymentId}" +
                $"&status={PaymentStatusMap.ToDb[outcome.Status]}",
                isPermanent: false,
                allowRemoteRedirects: true);
            return;
        }

        await Send.OkAsync(new CallbackHttpResponse(outcome.PaymentId, PaymentStatusMap.ToDb[outcome.Status]), ct);
    }
}
