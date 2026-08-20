using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Installments.Contracts;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Payments.Infrastructure;
using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Vault.Contracts;
using Poyra.Modules.Webhooks.Contracts;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Payments.Features.ConfirmDirect;

public sealed record ConfirmDirectRequest(
    string? CardNumber = null,
    int? ExpiryMonth = null,
    int? ExpiryYear = null,
    string? Cvv = null,
    string? HolderName = null,
    string? CardToken = null,
    string? Program = null,
    Guid? ConnectorAccountId = null,
    bool UseThreeDs = false);

public sealed record ConfirmDirectPaymentCommand(
    string PaymentPublicId,
    string? CardNumber,
    int? ExpiryMonth,
    int? ExpiryYear,
    string? Cvv,
    string? HolderName,
    string? CardToken,
    string? Program,
    Guid? ForceConnectorAccountId,
    bool UseThreeDs = false)
    : Poyra.SharedKernel.Cqrs.ICommand<PaymentResponse>;

public sealed class ConfirmDirectValidator : AbstractValidator<ConfirmDirectPaymentCommand>
{
    public ConfirmDirectValidator()
    {
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.CardNumber) ^ !string.IsNullOrWhiteSpace(x.CardToken))
            .WithMessage("Ya kart bilgileri ya da cardToken verilmeli (ikisi birden değil).");

        When(x => !string.IsNullOrWhiteSpace(x.CardNumber), () =>
        {
            RuleFor(x => x.CardNumber!).Must(CardNumbers.IsValidLuhn)
                .WithMessage("Kart numarası geçersiz (Luhn).");
            RuleFor(x => x.ExpiryMonth).NotNull().InclusiveBetween(1, 12);
            RuleFor(x => x.ExpiryYear).NotNull().InclusiveBetween(2000, 2099);
            RuleFor(x => x.Cvv).Matches("^[0-9]{3,4}$").When(x => x.Cvv is not null)
                .WithMessage("CVV 3-4 haneli olmalı.");
        });
    }
}

public sealed class ConfirmDirectPaymentHandler(
    PaymentsDbContext db,
    IRoutingService routing,
    IConnectorGateway gateway,
    IInstallmentPricing pricing,
    IBinLookup bins,
    ICardVault vault,
    IClock clock,
    IConfiguration configuration,
    PaymentMapper mapper,
    IRiskGate risk,
    IOutboxNudger outbox)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ConfirmDirectPaymentCommand, PaymentResponse>
{
    public async Task<PaymentResponse> Handle(ConfirmDirectPaymentCommand command, CancellationToken ct)
    {
        var intent = await db.PaymentIntents
                         .SingleOrDefaultAsync(p => p.PublicId == command.PaymentPublicId, ct)
                     ?? throw PoyraException.NotFound("payment.not_found", "Ödeme bulunamadı.");

        if (intent.Status != PaymentStatus.Created)
            throw new PoyraException(409, "payment.invalid_state",
                $"Ödeme '{PaymentStatusMap.ToDb[intent.Status]}' durumunda — confirm edilemez.");

        var card = await ResolveCardAsync(command, ct);

        var cardFacts = await CardFactsAsync(card.Pan[..Math.Min(8, card.Pan.Length)], command.Program, ct);

        await RiskGate.ApplyAsync(risk, db, intent, cardFacts,
            maskedPan: CardNumbers.Mask(card.Pan), flow: "direct", ct);

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
            flow = command.UseThreeDs ? "direct_3ds" : "direct",
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
            new { reason = decision.Reason, flow = command.UseThreeDs ? "direct_3ds" : "direct" }));

        var attemptNo = await db.PaymentAttempts.CountAsync(a => a.PaymentIntentId == intent.Id, ct) + 1;
        PaymentAttempt? lastAttempt = null;
        var skippedNoScheme = 0;
        var skippedNoDirect = 0;

        foreach (var accountId in decision.AccountIds.Take(Math.Max(1, decision.MaxAttempts)))
        {
            ResolvedConnectorAccount resolved;
            try
            {
                resolved = await gateway.ResolveAsync(accountId, ct);
            }
            catch (PoyraException)
            {
                continue;
            }

            if (intent.Installments > 1 && !resolved.Connector.Descriptor.SupportsInstallments)
            {
                skippedNoScheme++;
                continue;
            }

            var price = await pricing.PriceAsync(
                accountId, intent.Installments, intent.AmountMinor,
                cardFacts?.Program ?? command.Program, ct);
            if (price is null)
            {
                skippedNoScheme++;
                continue;
            }

            var attempt = PaymentAttempt.Open(intent, attemptNo++, resolved.Account.ConnectorKey, accountId,
                price.ChargedAmountMinor);
            attempt.AttachCardFacts(cardFacts?.Program, cardFacts?.BankCode, cardFacts?.Brand);
            db.PaymentAttempts.Add(attempt);

            CallbackToken? callbackToken = null;
            if (command.UseThreeDs)
            {
                callbackToken = CallbackToken.Create(intent, attempt, resolved.Account.ConnectorKey,
                    clock.UtcNow.AddMinutes(30));
                db.CallbackTokens.Add(callbackToken);
            }

            await db.SaveChangesAsync(ct); // att_… (bankaya gidecek sipariş no) üretilsin
            lastAttempt = attempt;

            if (command.UseThreeDs)
            {
                var threeDs = await TryInitiateThreeDsAsync(
                    intent, attempt, resolved, card, price, callbackToken!, ct);

                if (threeDs is ThreeDsOutcome.Unsupported)
                {
                    skippedNoDirect++;
                    continue;
                }

                if (threeDs is ThreeDsOutcome.Unavailable)
                    continue; // failover: sıradaki aday

                await db.SaveChangesAsync(ct);
                return PaymentPresenter.Present(mapper, intent, attempt);
            }

            DirectAuthorizeResult? result;
            var startedAt = clock.UtcNow;
            try
            {
                result = await resolved.Connector.AuthorizeDirectAsync(
                    new DirectPaymentRequest(
                        attempt.PublicId, price.ChargedAmountMinor, intent.Currency, intent.Installments,
                        card, intent.Description, CustomerIp: null),
                    resolved.Credentials, ct);
                attempt.LatencyMs = (int)(clock.UtcNow - startedAt).TotalMilliseconds;
            }
            catch (Exception ex) when (ex is ConnectorUnavailableException or ConnectorConfigurationException)
            {
                attempt.LatencyMs = (int)(clock.UtcNow - startedAt).TotalMilliseconds;
                attempt.MarkFailed(UnifiedErrors.ConnectorUnavailable, null, ex.Message);
                db.PaymentEvents.Add(PaymentEvent.For(intent, "attempt.failed", "system",
                    new { attempt = attempt.PublicId, error = UnifiedErrors.ConnectorUnavailable, failover = true }));
                await db.SaveChangesAsync(ct);
                continue; // failover: sıradaki aday
            }

            if (result is null)
            {
                attempt.MarkFailed(UnifiedErrors.NotPermitted, null, "Konnektör direct akışı desteklemiyor.");
                skippedNoDirect++;
                await db.SaveChangesAsync(ct);
                continue;
            }

            attempt.MarkDirectAuthorized(result.Success, result.AuthCode, result.ConnectorTxnId,
                result.MaskedPan, result.UnifiedCode, result.RawCode, result.RawMessage, clock.UtcNow);

            if (result.Success)
            {
                intent.MarkSucceededDirect();
                db.PaymentEvents.Add(PaymentEvent.For(intent, "payment.succeeded", "connector", new
                {
                    attempt = attempt.PublicId,
                    auth_code = result.AuthCode,
                    masked_pan = result.MaskedPan,
                    flow = "direct",
                }));
                db.OutboxMessages.Add(OutboxMessage.For(intent.TenantId, KnownWebhookEvents.PaymentSucceeded, new
                {
                    payment_id = intent.PublicId,
                    amount_minor = intent.AmountMinor,
                    charged_amount_minor = price.ChargedAmountMinor,
                    currency = intent.Currency,
                    installments = intent.Installments,
                    masked_pan = result.MaskedPan,
                }));
                await db.SaveChangesAsync(ct);
                outbox.Nudge();
                return PaymentPresenter.Present(mapper, intent, attempt);
            }

            if (UnifiedErrors.IsRetryableAtInitiate(result.UnifiedCode))
            {
                db.PaymentEvents.Add(PaymentEvent.For(intent, "attempt.failed", "connector", new
                {
                    attempt = attempt.PublicId,
                    error = result.UnifiedCode,
                    raw_code = result.RawCode,
                    failover = true,
                }));
                await db.SaveChangesAsync(ct);
                continue;
            }

            intent.MarkFailed();
            db.PaymentEvents.Add(PaymentEvent.For(intent, "payment.failed", "connector", new
            {
                attempt = attempt.PublicId,
                error = result.UnifiedCode,
                raw_code = result.RawCode,
                flow = "direct",
            }));
            db.OutboxMessages.Add(OutboxMessage.For(intent.TenantId, KnownWebhookEvents.PaymentFailed, new
            {
                payment_id = intent.PublicId,
                amount_minor = intent.AmountMinor,
                currency = intent.Currency,
                error = result.UnifiedCode,
            }));
            await db.SaveChangesAsync(ct);
            outbox.Nudge();
            return PaymentPresenter.Present(mapper, intent, attempt);
        }

        if (lastAttempt is null && skippedNoScheme > 0 && intent.Installments > 1)
            throw new PoyraException(409, "installments.not_offered",
                $"Aday POS hesaplarının hiçbiri {intent.Installments} taksit sunmuyor.");

        if (skippedNoDirect > 0 && lastAttempt?.Status == AttemptStatus.Failed)
            throw new PoyraException(409, "payments.direct_not_supported",
                "Aday POS hesapları kart-üzerinden (direct) akışı desteklemiyor — hosted confirm kullanın.");

        intent.MarkFailed();
        db.PaymentEvents.Add(PaymentEvent.For(intent, "payment.failed", "system",
            new { error = lastAttempt?.ErrorUnifiedCode ?? "routing.no_route" }));
        await db.SaveChangesAsync(ct);
        outbox.Nudge();

        return PaymentPresenter.Present(mapper, intent, lastAttempt);
    }

    private enum ThreeDsOutcome
    {
        Initiated,   // müşteri bankanın 3DS sayfasına gidiyor
        Unsupported, // konnektör 3DS'li direct sunmuyor → aday atlanır
        Unavailable, // erişim/yapılandırma hatası → failover
    }

    private async Task<ThreeDsOutcome> TryInitiateThreeDsAsync(
        PaymentIntent intent,
        PaymentAttempt attempt,
        ResolvedConnectorAccount resolved,
        CardData card,
        InstallmentPrice price,
        CallbackToken token,
        CancellationToken ct)
    {
        var baseUrl = configuration["Poyra:PublicBaseUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Poyra:PublicBaseUrl tanımlı değil (callback adresi için).");

        var startedAt = clock.UtcNow;
        try
        {
            var form = await resolved.Connector.InitiateThreeDsDirectAsync(
                new DirectPaymentRequest(
                    attempt.PublicId, price.ChargedAmountMinor, intent.Currency, intent.Installments,
                    card, intent.Description, CustomerIp: null),
                $"{baseUrl}/v1/callbacks/{resolved.Account.ConnectorKey}/{token.Token}",
                resolved.Credentials, ct);

            attempt.LatencyMs = (int)(clock.UtcNow - startedAt).TotalMilliseconds;

            if (form is null)
            {
                attempt.MarkFailed(UnifiedErrors.NotPermitted, null,
                    "Konnektör 3DS'li direct akışı desteklemiyor.");
                await db.SaveChangesAsync(ct);
                return ThreeDsOutcome.Unsupported;
            }

            attempt.MarkInitiated(form.ActionUrl, JsonSerializer.Serialize(form.Fields), form.Method,
                    form.ConnectorState is null ? null : JsonSerializer.Serialize(form.ConnectorState));
            intent.MarkRequiresAction();
            db.PaymentEvents.Add(PaymentEvent.For(intent, "attempt.initiated", "system", new
            {
                attempt = attempt.PublicId,
                connector = resolved.Account.Label,
                charged_amount_minor = price.ChargedAmountMinor,
                installment_rate_bps = price.RateBps,
                flow = "direct_3ds",
            }));

            return ThreeDsOutcome.Initiated;
        }
        catch (Exception ex) when (ex is ConnectorUnavailableException or ConnectorConfigurationException)
        {
            attempt.LatencyMs = (int)(clock.UtcNow - startedAt).TotalMilliseconds;
            attempt.MarkFailed(UnifiedErrors.ConnectorUnavailable, null, ex.Message);
            db.PaymentEvents.Add(PaymentEvent.For(intent, "attempt.failed", "system",
                new { attempt = attempt.PublicId, error = UnifiedErrors.ConnectorUnavailable, failover = true }));
            await db.SaveChangesAsync(ct);
            return ThreeDsOutcome.Unavailable;
        }
    }

    private async Task<CardFacts?> CardFactsAsync(string bin, string? program, CancellationToken ct)
        => await bins.FindAsync(bin, ct) is { } info
            ? new CardFacts(info.Bin, info.BankCode, info.Program, info.Brand, info.CardType, info.IsCommercial)
            : new CardFacts(bin, null, program, null, null, false); // katalogda yoksa yalnız BIN taşınır

    private async Task<CardData> ResolveCardAsync(ConfirmDirectPaymentCommand command, CancellationToken ct)
    {
        if (command.CardToken is { } token)
        {
            var vaulted = await vault.ResolveAsync(token, ct)
                ?? throw PoyraException.NotFound("vault.token_not_found", "Kart token'ı bulunamadı.");
            return vaulted.Card; // CVV yok — tekrarlayan ödeme kalıbı
        }

        return new CardData(
            command.CardNumber!.Replace(" ", "").Replace("-", ""),
            command.ExpiryMonth!.Value, command.ExpiryYear!.Value, command.HolderName, command.Cvv);
    }

    private static int TurkeyHour(IClock clock) => clock.UtcNow.UtcDateTime.AddHours(3).Hour;
}

public sealed class ConfirmDirectPaymentEndpoint(IDispatcher dispatcher)
    : Endpoint<ConfirmDirectRequest, PaymentResponse>
{
    public override void Configure()
    {
        Post("/v1/payments/{id}/confirm-direct");
        Description(x => x.WithTags("Payments"));
        Summary(s => s.Summary =
            "Kart bilgisi veya kayıtlı kart token'ı ile satış. PCI kapsamındadır. "
            + "useThreeDs=true ise müşteri bankanın 3DS sayfasına yönlendirilir (requires_action + form) "
            + "ve chargeback sorumluluğu bankaya geçer; false ise tek adımda sonuçlanır.");
    }

    public override async Task HandleAsync(ConfirmDirectRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new ConfirmDirectPaymentCommand(
            Route<string>("id")!, req.CardNumber, req.ExpiryMonth, req.ExpiryYear, req.Cvv,
            req.HolderName, req.CardToken, req.Program, req.ConnectorAccountId, req.UseThreeDs), ct), ct);
}
