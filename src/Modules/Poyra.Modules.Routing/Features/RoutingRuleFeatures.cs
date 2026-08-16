using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Routing.Domain;
using Poyra.Modules.Routing.Dsl;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Routing.Features;

public sealed record RoutingRuleResponse(Guid Id, string Name, int Version, bool IsActive, JsonElement Document);

internal static class RoutingRuleMap
{
    public static RoutingRuleResponse ToResponse(RoutingRule rule)
        => new(rule.Id, rule.Name, rule.Version, rule.IsActive, JsonDocument.Parse(rule.Document).RootElement.Clone());
}

public sealed record CreateRoutingRuleRequest(string Name, JsonElement Document);

public sealed record CreateRoutingRuleCommand(string Name, JsonElement Document)
    : Poyra.SharedKernel.Cqrs.ICommand<RoutingRuleResponse>;

public sealed class CreateRoutingRuleValidator : AbstractValidator<CreateRoutingRuleCommand>
{
    public CreateRoutingRuleValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(100);
        RuleFor(x => x.Document).Must(BeParseable)
            .WithMessage("Kural dokümanı geçersiz — Dsl/RuleDocument şemasına uymalı.");
    }

    private static bool BeParseable(JsonElement document)
    {
        try
        {
            var parsed = RuleDocument.Parse(document.GetRawText());
            return parsed.VolumeSplit.Count == 0 || parsed.VolumeSplit.Sum(v => v.Percent) <= 100;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class CreateRoutingRuleHandler(RoutingDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreateRoutingRuleCommand, RoutingRuleResponse>
{
    public async Task<RoutingRuleResponse> Handle(CreateRoutingRuleCommand command, CancellationToken ct)
    {
        var lastVersion = await db.RoutingRules
            .Where(r => r.Name == command.Name)
            .MaxAsync(r => (int?)r.Version, ct) ?? 0;

        var rule = new RoutingRule
        {
            TenantId = tenant.TenantId,
            Name = command.Name,
            Version = lastVersion + 1,
            Document = command.Document.GetRawText(),
            IsActive = false, // aktivasyon ayrı, bilinçli bir adımdır
        };

        db.RoutingRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return RoutingRuleMap.ToResponse(rule);
    }
}

public sealed class CreateRoutingRuleEndpoint(IDispatcher dispatcher)
    : Endpoint<CreateRoutingRuleRequest, RoutingRuleResponse>
{
    public override void Configure()
    {
        Post("/v1/routing/rules");
        Description(x => x.WithTags("Routing"));
        Summary(s => s.Summary = "Yeni kural sürümü oluşturur (pasif). Aktivasyon ayrı uçtan yapılır.");
    }

    public override async Task HandleAsync(CreateRoutingRuleRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new CreateRoutingRuleCommand(req.Name, req.Document), ct), ct);
}

public sealed record ActivateRoutingRuleCommand(Guid RuleId) : Poyra.SharedKernel.Cqrs.ICommand<RoutingRuleResponse>;

public sealed class ActivateRoutingRuleHandler(RoutingDbContext db)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ActivateRoutingRuleCommand, RoutingRuleResponse>
{
    public async Task<RoutingRuleResponse> Handle(ActivateRoutingRuleCommand command, CancellationToken ct)
    {
        var rule = await db.RoutingRules.SingleOrDefaultAsync(r => r.Id == command.RuleId, ct)
            ?? throw PoyraException.NotFound("routing_rule.not_found", "Kural bulunamadı.");

        var current = await db.RoutingRules.Where(r => r.IsActive && r.Id != rule.Id).ToListAsync(ct);
        foreach (var previous in current)
            previous.IsActive = false;

        rule.IsActive = true;
        await db.SaveChangesAsync(ct);
        return RoutingRuleMap.ToResponse(rule);
    }
}

public sealed class ActivateRoutingRuleEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<RoutingRuleResponse>
{
    public override void Configure()
    {
        Post("/v1/routing/rules/{id}/activate");
        Description(x => x.WithTags("Routing"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new ActivateRoutingRuleCommand(Route<Guid>("id")), ct), ct);
}

public sealed record ListRoutingRulesQuery : IQuery<IReadOnlyList<RoutingRuleResponse>>;

public sealed class ListRoutingRulesHandler(RoutingDbContext db)
    : IQueryHandler<ListRoutingRulesQuery, IReadOnlyList<RoutingRuleResponse>>
{
    public async Task<IReadOnlyList<RoutingRuleResponse>> Handle(ListRoutingRulesQuery query, CancellationToken ct)
    {
        // İşyeri filtresi DbContext'in RLS bağlamından gelir; eski sürümler silinmez,
        // bu liste "geri dönülecek sürüm" seçimi içindir.
        var rules = await db.RoutingRules.AsNoTracking()
            .OrderBy(r => r.Name).ThenByDescending(r => r.Version)
            .ToListAsync(ct);

        return [.. rules.Select(RoutingRuleMap.ToResponse)];
    }
}

public sealed record GetActiveRuleQuery : IQuery<RoutingRuleResponse?>;

public sealed class GetActiveRuleHandler(RoutingDbContext db)
    : IQueryHandler<GetActiveRuleQuery, RoutingRuleResponse?>
{
    public async Task<RoutingRuleResponse?> Handle(GetActiveRuleQuery query, CancellationToken ct)
    {
        var rule = await db.RoutingRules.AsNoTracking().SingleOrDefaultAsync(r => r.IsActive, ct);
        return rule is null ? null : RoutingRuleMap.ToResponse(rule);
    }
}

public sealed class GetActiveRuleEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<RoutingRuleResponse>
{
    public override void Configure()
    {
        Get("/v1/routing/rules/active");
        Description(x => x.WithTags("Routing"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await dispatcher.Ask(new GetActiveRuleQuery(), ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(result, ct);
    }
}
