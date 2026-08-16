using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Subscriptions.Contracts;
using Poyra.Modules.Subscriptions.Domain;
using Poyra.Modules.Subscriptions.Infrastructure;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Subscriptions.Features;

public sealed record SubscriptionDto(
    string Id, string PlanId, string CustomerRef, string Status, string CardToken,
    DateTimeOffset CurrentPeriodStart, DateTimeOffset CurrentPeriodEnd,
    bool CancelAtPeriodEnd, bool NeedsCardUpdate);

internal static class SubscriptionMap
{
    public static SubscriptionDto ToDto(Subscription s, string planPublicId)
        => new(s.PublicId, planPublicId, s.CustomerRef, SubscriptionStatusMap.ToDb[s.Status], s.CardToken,
            s.CurrentPeriodStart, s.CurrentPeriodEnd, s.CancelAtPeriodEnd, s.NeedsCardUpdate);
}

public sealed record CreateSubscriptionRequest(
    string PlanId, string CustomerRef, string CardToken, bool ChargeImmediately = true);

public sealed record CreateSubscriptionCommand(
    string PlanId, string CustomerRef, string CardToken, bool ChargeImmediately)
    : Poyra.SharedKernel.Cqrs.ICommand<SubscriptionDto>;

public sealed class CreateSubscriptionValidator : AbstractValidator<CreateSubscriptionCommand>
{
    public CreateSubscriptionValidator()
    {
        RuleFor(x => x.PlanId).NotEmpty();
        RuleFor(x => x.CustomerRef).NotEmpty().MaximumLength(100);
        RuleFor(x => x.CardToken).NotEmpty().Must(t => t.StartsWith("tok_"))
            .WithMessage("cardToken Kasa token'ı olmalı (tok_…).");
    }
}

public sealed class CreateSubscriptionHandler(
    SubscriptionsDbContext db, TenantContext tenant, SubscriptionBiller biller, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreateSubscriptionCommand, SubscriptionDto>
{
    public async Task<SubscriptionDto> Handle(CreateSubscriptionCommand command, CancellationToken ct)
    {
        var plan = await db.Plans.AsNoTracking()
                       .SingleOrDefaultAsync(p => p.PublicId == command.PlanId && p.Active, ct)
                   ?? throw PoyraException.NotFound("subscription.plan_not_found", "Plan bulunamadı.");

        var now = clock.UtcNow;
        var subscription = new Subscription
        {
            TenantId = tenant.TenantId,
            PlanId = plan.Id,
            CustomerRef = command.CustomerRef,
            CardToken = command.CardToken,
            CurrentPeriodStart = now,
            Status = plan.TrialDays > 0 ? SubscriptionStatus.Trialing : SubscriptionStatus.Active,
            // Deneme varsa ilk tahakkuk deneme sonunda; yoksa dönem hemen başlar
            CurrentPeriodEnd = plan.TrialDays > 0
                ? now.AddDays(plan.TrialDays)
                : BillingIntervalMap.Advance(now, plan.Interval, plan.IntervalCount),
        };

        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(ct); // sub_… üretilsin

        if (plan.TrialDays == 0 && command.ChargeImmediately)
        {
            var invoice = new SubscriptionInvoice
            {
                TenantId = tenant.TenantId,
                SubscriptionId = subscription.Id,
                PeriodStart = now,
                PeriodEnd = subscription.CurrentPeriodEnd,
                AmountMinor = plan.AmountMinor,
                Currency = plan.Currency,
            };
            db.SubscriptionInvoices.Add(invoice);
            await db.SaveChangesAsync(ct);

            await biller.ChargeInvoiceAsync(subscription, invoice, ct);
        }

        return SubscriptionMap.ToDto(subscription, plan.PublicId);
    }
}

public sealed class CreateSubscriptionEndpoint(IDispatcher dispatcher)
    : Endpoint<CreateSubscriptionRequest, SubscriptionDto>
{
    public override void Configure()
    {
        Post("/v1/subscriptions");
        Description(x => x.WithTags("Subscriptions"));
        Summary(s => s.Summary = "Kayıtlı kartla abonelik başlatır; deneme yoksa ilk dönem hemen tahsil edilir.");
    }

    public override async Task HandleAsync(CreateSubscriptionRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new CreateSubscriptionCommand(
            req.PlanId, req.CustomerRef, req.CardToken, req.ChargeImmediately), ct), ct);
}

public sealed record ListSubscriptionsQuery(string? CustomerRef, string? Status)
    : IQuery<IReadOnlyList<SubscriptionDto>>;

public sealed class ListSubscriptionsHandler(SubscriptionsDbContext db)
    : IQueryHandler<ListSubscriptionsQuery, IReadOnlyList<SubscriptionDto>>
{
    public async Task<IReadOnlyList<SubscriptionDto>> Handle(ListSubscriptionsQuery query, CancellationToken ct)
    {
        var subscriptions = db.Subscriptions.AsNoTracking();
        if (query.CustomerRef is { } customerRef)
            subscriptions = subscriptions.Where(s => s.CustomerRef == customerRef);
        if (query.Status is { } status && SubscriptionStatusMap.FromDb.TryGetValue(status, out var parsed))
            subscriptions = subscriptions.Where(s => s.Status == parsed);

        return await (
                from s in subscriptions
                join p in db.Plans.AsNoTracking() on s.PlanId equals p.Id
                orderby s.CreatedAt descending
                select new SubscriptionDto(
                    s.PublicId, p.PublicId, s.CustomerRef,
                    s.Status == SubscriptionStatus.Trialing ? "trialing"
                    : s.Status == SubscriptionStatus.Active ? "active"
                    : s.Status == SubscriptionStatus.PastDue ? "past_due"
                    : s.Status == SubscriptionStatus.Paused ? "paused"
                    : s.Status == SubscriptionStatus.Cancelled ? "cancelled" : "unpaid",
                    s.CardToken, s.CurrentPeriodStart, s.CurrentPeriodEnd,
                    s.CancelAtPeriodEnd, s.NeedsCardUpdate))
            .ToListAsync(ct);
    }
}

public sealed class ListSubscriptionsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<SubscriptionDto>>
{
    public override void Configure()
    {
        Get("/v1/subscriptions");
        Description(x => x.WithTags("Subscriptions"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListSubscriptionsQuery(
            Query<string?>("customerRef", isRequired: false),
            Query<string?>("status", isRequired: false)), ct), ct);
}

public sealed record UpdateCardRequest(string CardToken);

public sealed record UpdateSubscriptionCardCommand(string SubscriptionId, string CardToken)
    : Poyra.SharedKernel.Cqrs.ICommand<SubscriptionDto>;

public sealed class UpdateSubscriptionCardHandler(SubscriptionsDbContext db, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<UpdateSubscriptionCardCommand, SubscriptionDto>
{
    public async Task<SubscriptionDto> Handle(UpdateSubscriptionCardCommand command, CancellationToken ct)
    {
        var subscription = await db.Subscriptions
                               .SingleOrDefaultAsync(s => s.PublicId == command.SubscriptionId, ct)
                           ?? throw PoyraException.NotFound("subscription.not_found", "Abonelik bulunamadı.");

        subscription.CardToken = command.CardToken;
        subscription.NeedsCardUpdate = false;

        var pending = await db.SubscriptionInvoices
            .Where(i => i.SubscriptionId == subscription.Id && i.Status == InvoiceStatus.Retrying)
            .ToListAsync(ct);
        foreach (var invoice in pending)
            invoice.NextRetryAt = clock.UtcNow; // sıradaki dunning turunda hemen denensin

        await db.SaveChangesAsync(ct);

        var plan = await db.Plans.AsNoTracking().SingleAsync(p => p.Id == subscription.PlanId, ct);
        return SubscriptionMap.ToDto(subscription, plan.PublicId);
    }
}

public sealed class UpdateSubscriptionCardEndpoint(IDispatcher dispatcher)
    : Endpoint<UpdateCardRequest, SubscriptionDto>
{
    public override void Configure()
    {
        Post("/v1/subscriptions/{id}/card");
        Description(x => x.WithTags("Subscriptions"));
        Summary(s => s.Summary = "Aboneliğin kartını günceller ve bekleyen tahsilatı hemen kuyruğa alır.");
    }

    public override async Task HandleAsync(UpdateCardRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new UpdateSubscriptionCardCommand(Route<string>("id")!, req.CardToken), ct), ct);
}

public sealed record ChangeSubscriptionStateRequest(bool AtPeriodEnd = true);

public sealed record CancelSubscriptionCommand(string SubscriptionId, bool AtPeriodEnd)
    : Poyra.SharedKernel.Cqrs.ICommand<SubscriptionDto>;

public sealed class CancelSubscriptionHandler(
    SubscriptionsDbContext db, IWebhookPublisher publisher, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CancelSubscriptionCommand, SubscriptionDto>
{
    public async Task<SubscriptionDto> Handle(CancelSubscriptionCommand command, CancellationToken ct)
    {
        var subscription = await db.Subscriptions
                               .SingleOrDefaultAsync(s => s.PublicId == command.SubscriptionId, ct)
                           ?? throw PoyraException.NotFound("subscription.not_found", "Abonelik bulunamadı.");

        if (subscription.Status == SubscriptionStatus.Cancelled)
            throw new PoyraException(409, "subscription.already_cancelled", "Abonelik zaten iptal edilmiş.");

        if (command.AtPeriodEnd)
        {
            // Dönem sonunda kapanır — müşteri ödediği süreyi kullanır
            subscription.CancelAtPeriodEnd = true;
        }
        else
        {
            subscription.Status = SubscriptionStatus.Cancelled;
            subscription.CancelledAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        if (!command.AtPeriodEnd)
            await publisher.PublishAsync(subscription, KnownSubscriptionEvents.Cancelled,
                new { reason = "immediate" }, ct);

        var plan = await db.Plans.AsNoTracking().SingleAsync(p => p.Id == subscription.PlanId, ct);
        return SubscriptionMap.ToDto(subscription, plan.PublicId);
    }
}

public sealed class CancelSubscriptionEndpoint(IDispatcher dispatcher)
    : Endpoint<ChangeSubscriptionStateRequest, SubscriptionDto>
{
    public override void Configure()
    {
        Post("/v1/subscriptions/{id}/cancel");
        Description(x => x.WithTags("Subscriptions"));
        Summary(s => s.Summary = "Aboneliği iptal eder (varsayılan: dönem sonunda).");
    }

    public override async Task HandleAsync(ChangeSubscriptionStateRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new CancelSubscriptionCommand(Route<string>("id")!, req.AtPeriodEnd), ct), ct);
}

public sealed record InvoiceDto(
    string Id, string SubscriptionId, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    long AmountMinor, string Status, int AttemptCount, DateTimeOffset? NextRetryAt,
    string? LastPaymentId, string? LastErrorCode, DateTimeOffset? PaidAt);

public sealed record ListInvoicesQuery(string? SubscriptionId) : IQuery<IReadOnlyList<InvoiceDto>>;

public sealed class ListInvoicesHandler(SubscriptionsDbContext db)
    : IQueryHandler<ListInvoicesQuery, IReadOnlyList<InvoiceDto>>
{
    public async Task<IReadOnlyList<InvoiceDto>> Handle(ListInvoicesQuery query, CancellationToken ct)
        => await (
                from i in db.SubscriptionInvoices.AsNoTracking()
                join s in db.Subscriptions.AsNoTracking() on i.SubscriptionId equals s.Id
                where query.SubscriptionId == null || s.PublicId == query.SubscriptionId
                orderby i.CreatedAt descending
                select new InvoiceDto(
                    i.PublicId, s.PublicId, i.PeriodStart, i.PeriodEnd, i.AmountMinor,
                    i.Status == InvoiceStatus.Pending ? "pending"
                    : i.Status == InvoiceStatus.Paid ? "paid"
                    : i.Status == InvoiceStatus.Retrying ? "retrying" : "abandoned",
                    i.AttemptCount, i.NextRetryAt, i.LastPaymentId, i.LastErrorCode, i.PaidAt))
            .Take(200)
            .ToListAsync(ct);
}

public sealed class ListInvoicesEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<IReadOnlyList<InvoiceDto>>
{
    public override void Configure()
    {
        Get("/v1/subscription-invoices");
        Description(x => x.WithTags("Subscriptions"));
        Summary(s => s.Summary = "Abonelik faturaları — dunning durumu, deneme sayısı ve son hata koduyla.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(
            new ListInvoicesQuery(Query<string?>("subscriptionId", isRequired: false)), ct), ct);
}
