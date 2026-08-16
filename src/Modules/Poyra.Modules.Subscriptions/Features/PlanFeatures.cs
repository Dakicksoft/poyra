using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Subscriptions.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Subscriptions.Features;

public sealed record PlanDto(
    string Id, string Name, long AmountMinor, string Currency,
    string Interval, int IntervalCount, int TrialDays, bool Active);

internal static class PlanMap
{
    public static PlanDto ToDto(Plan p)
        => new(p.PublicId, p.Name, p.AmountMinor, p.Currency,
            BillingIntervalMap.ToDb[p.Interval], p.IntervalCount, p.TrialDays, p.Active);
}

public sealed record CreatePlanRequest(
    string Name, long AmountMinor, string Currency = "TRY",
    string Interval = "month", int IntervalCount = 1, int TrialDays = 0);

public sealed record CreatePlanCommand(
    string Name, long AmountMinor, string Currency, string Interval, int IntervalCount, int TrialDays)
    : Poyra.SharedKernel.Cqrs.ICommand<PlanDto>;

public sealed class CreatePlanValidator : AbstractValidator<CreatePlanCommand>
{
    public CreatePlanValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.AmountMinor).GreaterThan(0);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.Interval).Must(BillingIntervalMap.FromDb.ContainsKey)
            .WithMessage($"Geçersiz periyot. Geçerli: {string.Join(", ", BillingIntervalMap.FromDb.Keys)}");
        RuleFor(x => x.IntervalCount).InclusiveBetween(1, 24);
        RuleFor(x => x.TrialDays).InclusiveBetween(0, 365);
    }
}

public sealed class CreatePlanHandler(SubscriptionsDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreatePlanCommand, PlanDto>
{
    public async Task<PlanDto> Handle(CreatePlanCommand command, CancellationToken ct)
    {
        var plan = new Plan
        {
            TenantId = tenant.TenantId,
            Name = command.Name,
            AmountMinor = command.AmountMinor,
            Currency = command.Currency.ToUpperInvariant(),
            Interval = BillingIntervalMap.FromDb[command.Interval],
            IntervalCount = command.IntervalCount,
            TrialDays = command.TrialDays,
        };

        db.Plans.Add(plan);
        await db.SaveChangesAsync(ct);
        return PlanMap.ToDto(plan);
    }
}

public sealed class CreatePlanEndpoint(IDispatcher dispatcher) : Endpoint<CreatePlanRequest, PlanDto>
{
    public override void Configure()
    {
        Post("/v1/plans");
        Description(x => x.WithTags("Subscriptions"));
        Summary(s => s.Summary = "Abonelik planı oluşturur (tutar + periyot + deneme süresi).");
    }

    public override async Task HandleAsync(CreatePlanRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new CreatePlanCommand(
            req.Name, req.AmountMinor, req.Currency, req.Interval, req.IntervalCount, req.TrialDays), ct), ct);
}

public sealed record ListPlansQuery : IQuery<IReadOnlyList<PlanDto>>;

public sealed class ListPlansHandler(SubscriptionsDbContext db)
    : IQueryHandler<ListPlansQuery, IReadOnlyList<PlanDto>>
{
    public async Task<IReadOnlyList<PlanDto>> Handle(ListPlansQuery query, CancellationToken ct)
        => await db.Plans.AsNoTracking().OrderBy(p => p.CreatedAt)
            .Select(p => PlanMap.ToDto(p)).ToListAsync(ct);
}

public sealed class ListPlansEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<IReadOnlyList<PlanDto>>
{
    public override void Configure()
    {
        Get("/v1/plans");
        Description(x => x.WithTags("Subscriptions"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListPlansQuery(), ct), ct);
}

public sealed record SetPlanStatusRequest(bool Active);

public sealed record SetPlanStatusCommand(string PlanId, bool Active)
    : Poyra.SharedKernel.Cqrs.ICommand<PlanDto>;


public sealed class SetPlanStatusHandler(SubscriptionsDbContext db)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SetPlanStatusCommand, PlanDto>
{
    public async Task<PlanDto> Handle(SetPlanStatusCommand command, CancellationToken ct)
    {
        var plan = await db.Plans.SingleOrDefaultAsync(p => p.PublicId == command.PlanId, ct)
            ?? throw Poyra.SharedKernel.Errors.PoyraException.NotFound(
                "subscription.plan_not_found", "Plan bulunamadı.");

        plan.Active = command.Active;
        await db.SaveChangesAsync(ct);
        return PlanMap.ToDto(plan);
    }
}

public sealed class SetPlanStatusEndpoint(IDispatcher dispatcher) : Endpoint<SetPlanStatusRequest, PlanDto>
{
    public override void Configure()
    {
        Post("/v1/plans/{id}/status");
        Description(x => x.WithTags("Subscriptions"));
        Summary(s => s.Summary =
            "Planı kapatır/açar. Kapalı plana yeni abonelik açılamaz; mevcut aboneler etkilenmez.");
    }

    public override async Task HandleAsync(SetPlanStatusRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new SetPlanStatusCommand(Route<string>("id")!, req.Active), ct), ct);
}
