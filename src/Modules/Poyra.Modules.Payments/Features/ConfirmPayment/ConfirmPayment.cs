using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Installments.Contracts;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Payments.Infrastructure;
using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Webhooks.Contracts;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Payments.Features.ConfirmPayment;

public sealed record ConfirmPaymentRequest(
    Guid? ConnectorAccountId = null, string? Program = null, string? Bin = null);

public sealed record ConfirmPaymentCommand(
    string PaymentPublicId, Guid? ForceConnectorAccountId, string? Program, string? Bin = null)
    : Poyra.SharedKernel.Cqrs.ICommand<PaymentResponse>;

public sealed class ConfirmPaymentHandler(
    PaymentsDbContext db,
    IRoutingService routing,
    IConnectorGateway gateway,
    IInstallmentPricing pricing,
    IBinLookup bins,
    IClock clock,
    IConfiguration configuration,
    PaymentMapper mapper,
    IRiskGate risk,
    IOutboxNudger outbox)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ConfirmPaymentCommand, PaymentResponse>
{
    public async Task<PaymentResponse> Handle(ConfirmPaymentCommand command, CancellationToken ct)
    {
        var intent = await db.PaymentIntents
                         .SingleOrDefaultAsync(p => p.PublicId == command.PaymentPublicId, ct)
                     ?? throw PoyraException.NotFound("payment.not_found", "Ödeme bulunamadı.");

        if (intent.Status == PaymentStatus.RequiresAction)
            return PaymentPresenter.Present(mapper, intent, await LatestAttempt(intent.Id, ct)); // idempotent

        if (intent.Status != PaymentStatus.Created)
            throw new PoyraException(409, "payment.invalid_state",
                $"Ödeme '{PaymentStatusMap.ToDb[intent.Status]}' durumunda — confirm edilemez.");

        var cardFacts = await CardFactsAsync(command.Bin, command.Program, ct);
        var program = cardFacts?.Program ?? command.Program;

        await RiskGate.ApplyAsync(risk, db, intent, cardFacts, maskedPan: null,
            flow: "hosted", ct);

        var decision = command.ForceConnectorAccountId is { } forced
            ? new RoutingDecision([forced], "Elle seçilen bağlantı hesabı.", 1, null, null)
            : await routing.DecideAsync(new RoutingFacts(
                intent.Id, intent.AmountMinor, intent.Currency, intent.Installments,
                TurkeyHour(clock), cardFacts), ct);

        if (decision.AccountIds.Count == 0)
            throw new PoyraException(409, "routing.no_route", decision.Reason);

        intent.RoutingResultJson = JsonSerializer.Serialize(new
        {
            reason = decision.Reason,
            rule = decision.RuleName,
            rule_version = decision.RuleVersion,
            strategy = decision.Strategy,
            candidates = decision.AccountIds,
            flow = "hosted",
            forced = command.ForceConnectorAccountId is not null, // elle sabitlendi — kural devrede değildi
            card = DecisionCardJson.From(cardFacts), // karar anındaki kart — simülatör replay'i için
            signals = decision.Candidates?.Select(c => new
            {
                account = c.Label,
                expected_cost_minor = c.ExpectedCostMinor,
                auth_rate = c.AuthRate,
                median_latency_ms = c.MedianLatencyMs,
            }),
        });
        db.PaymentEvents.Add(PaymentEvent.For(intent, "payment.confirm.started", "api",
            new { reason = decision.Reason }));

        var baseUrl = configuration["Poyra:PublicBaseUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Poyra:PublicBaseUrl tanımlı değil (callback adresi için gerekli).");

        var attemptNo = await db.PaymentAttempts.CountAsync(a => a.PaymentIntentId == intent.Id, ct) + 1;
        PaymentAttempt? lastAttempt = null;
        var skippedNoScheme = 0;

        foreach (var accountId in decision.AccountIds.Take(Math.Max(1, decision.MaxAttempts)))
        {
            ResolvedConnectorAccount resolved;
            try
            {
                resolved = await gateway.ResolveAsync(accountId, ct);
            }
            catch (PoyraException)
            {
                continue; // hesap bu arada kapatılmış olabilir — sıradaki adaya geç
            }

            if (intent.Installments > 1 && !resolved.Connector.Descriptor.SupportsInstallments)
            {
                skippedNoScheme++;
                continue;
            }

            var price = await pricing.PriceAsync(
                accountId, intent.Installments, intent.AmountMinor, program, ct);
            if (price is null)
            {
                skippedNoScheme++;
                continue;
            }

            var attempt = PaymentAttempt.Open(intent, attemptNo++, resolved.Account.ConnectorKey, accountId,
                price.ChargedAmountMinor);
            attempt.AttachCardFacts(cardFacts?.Program, cardFacts?.BankCode, cardFacts?.Brand);
            var token = CallbackToken.Create(intent, attempt, resolved.Account.ConnectorKey,
                clock.UtcNow.AddMinutes(30));
            db.PaymentAttempts.Add(attempt);
            db.CallbackTokens.Add(token);
            await db.SaveChangesAsync(ct); // att_… (bankaya gidecek oid) DB'de üretilsin
            lastAttempt = attempt;

            var startedAt = clock.UtcNow;
            try
            {
                var form = await resolved.Connector.InitiateHostedPaymentAsync(
                    new HostedPaymentRequest(
                        attempt.PublicId, price.ChargedAmountMinor, intent.Currency, intent.Installments,
                        $"{baseUrl}/v1/callbacks/{resolved.Account.ConnectorKey}/{token.Token}",
                        intent.Description, CustomerIp: null),
                    resolved.Credentials, ct);

                // Gecikme ölçümü rota motorunun "en hızlı yanıt" sinyalini besler
                attempt.LatencyMs = (int)(clock.UtcNow - startedAt).TotalMilliseconds;

                attempt.MarkInitiated(form.ActionUrl, JsonSerializer.Serialize(form.Fields), form.Method,
                    form.ConnectorState is null ? null : JsonSerializer.Serialize(form.ConnectorState));
                intent.MarkRequiresAction();
                db.PaymentEvents.Add(PaymentEvent.For(intent, "attempt.initiated", "system", new
                {
                    attempt = attempt.PublicId,
                    connector = resolved.Account.Label,
                    charged_amount_minor = price.ChargedAmountMinor,
                    installment_rate_bps = price.RateBps,
                }));
                await db.SaveChangesAsync(ct);

                return PaymentPresenter.Present(mapper, intent, attempt);
            }
            catch (Exception ex) when (ex is ConnectorUnavailableException or ConnectorConfigurationException)
            {
                attempt.LatencyMs = (int)(clock.UtcNow - startedAt).TotalMilliseconds;
                attempt.MarkFailed(UnifiedErrors.ConnectorUnavailable, null, ex.Message);
                db.PaymentEvents.Add(PaymentEvent.For(intent, "attempt.failed", "system",
                    new { attempt = attempt.PublicId, error = UnifiedErrors.ConnectorUnavailable, failover = true }));
                await db.SaveChangesAsync(ct); // failover: sıradaki adaya geç
            }
        }

        if (lastAttempt is null && skippedNoScheme > 0 && intent.Installments > 1)
            throw new PoyraException(409, "installments.not_offered",
                $"Aday POS hesaplarının hiçbiri {intent.Installments} taksit sunmuyor — "
                + "taksit şeması tanımlayın (POST /v1/installments/schemes) veya taksidi değiştirin.");

        intent.MarkFailed();
        db.PaymentEvents.Add(PaymentEvent.For(intent, "payment.failed", "system",
            new { error = lastAttempt?.ErrorUnifiedCode ?? "routing.no_route" }));
        db.OutboxMessages.Add(OutboxMessage.For(intent.TenantId, KnownWebhookEvents.PaymentFailed, new
        {
            payment_id = intent.PublicId,
            amount_minor = intent.AmountMinor,
            currency = intent.Currency,
            error = lastAttempt?.ErrorUnifiedCode ?? "routing.no_route",
        }));
        await db.SaveChangesAsync(ct);
        outbox.Nudge();

        return PaymentPresenter.Present(mapper, intent, lastAttempt);
    }

    private async Task<CardFacts?> CardFactsAsync(string? bin, string? program, CancellationToken ct)
    {
        // Tarayıcı kontrolündeki ham girdi: yalnız 6+ haneli rakam dizisi BIN sayılır ve ilk
        // 8 hanede kesilir — bin alanına yanlışlıkla/kötü niyetle basılan tam PAN ne kural
        // fact'lerine ne kayıtlara sızar (kalıcılaştıran taraf ayrıca 6 haneye kırpar).
        bin = bin is { Length: >= 6 } && bin.All(char.IsAsciiDigit)
            ? bin[..Math.Min(8, bin.Length)]
            : null;

        if (bin is not null && await bins.FindAsync(bin, ct) is { } info)
            return new CardFacts(info.Bin, info.BankCode, info.Program, info.Brand,
                info.CardType, info.IsCommercial);

        return bin is null && program is null
            ? null
            : new CardFacts(bin, null, program, null, null, false);
    }

    private async Task<PaymentAttempt?> LatestAttempt(Guid intentId, CancellationToken ct)
        => await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.PaymentIntentId == intentId)
            .OrderByDescending(a => a.AttemptNo)
            .FirstOrDefaultAsync(ct);

    private static int TurkeyHour(IClock clock) => clock.UtcNow.UtcDateTime.AddHours(3).Hour;
}

public sealed class ConfirmPaymentEndpoint(IDispatcher dispatcher)
    : Endpoint<ConfirmPaymentRequest, PaymentResponse>
{
    public override void Configure()
    {
        Post("/v1/payments/{id}/confirm");
        Description(x => x.WithTags("Payments"));
        Summary(s => s.Summary = "Ödemeyi rotaya sokar; müşterinin bankaya POST edeceği formu (next_action) döner.");
    }

    public override async Task HandleAsync(ConfirmPaymentRequest req, CancellationToken ct)
    {
        var response = await dispatcher.Send(
            new ConfirmPaymentCommand(Route<string>("id")!, req.ConnectorAccountId, req.Program, req.Bin), ct);
        await Send.OkAsync(response, ct);
    }
}
