using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Routing.Infrastructure;
using Poyra.SharedKernel.Time;
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

// --- Hacim taahhütleri ------------------------------------------------------

public sealed record VolumeCommitmentDto(
    Guid Id, Guid ConnectorAccountId, long MonthlyTargetMinor, bool IsActive,
    long AchievedMinor, long GapMinor, int DaysLeft);

public sealed record UpsertVolumeCommitmentRequest(Guid ConnectorAccountId, long MonthlyTargetMinor);

public sealed record UpsertVolumeCommitmentCommand(Guid ConnectorAccountId, long MonthlyTargetMinor)
    : Poyra.SharedKernel.Cqrs.ICommand<VolumeCommitmentDto>;

public sealed class UpsertVolumeCommitmentValidator : AbstractValidator<UpsertVolumeCommitmentCommand>
{
    public UpsertVolumeCommitmentValidator()
        => RuleFor(x => x.MonthlyTargetMinor)
            .GreaterThan(0)
            .WithMessage("Aylık hedef kuruş cinsinden ve 0'dan büyük olmalı. "
                         + "Taahhüdü kaldırmak için DELETE kullanın (kayıt pasife çekilir).");
}

public sealed class UpsertVolumeCommitmentHandler(RoutingDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<UpsertVolumeCommitmentCommand, VolumeCommitmentDto>
{
    public async Task<VolumeCommitmentDto> Handle(
        UpsertVolumeCommitmentCommand command, CancellationToken ct)
    {
        var commitment = await db.VolumeCommitments
            .SingleOrDefaultAsync(c => c.ConnectorAccountId == command.ConnectorAccountId, ct);

        if (commitment is null)
        {
            commitment = new VolumeCommitment
            {
                TenantId = tenant.TenantId,
                ConnectorAccountId = command.ConnectorAccountId,
            };
            db.VolumeCommitments.Add(commitment);
        }

        commitment.MonthlyTargetMinor = command.MonthlyTargetMinor;
        commitment.IsActive = true; // pasife çekilmiş taahhüt yeniden tanımlanırsa canlanır
        await db.SaveChangesAsync(ct);

        return new VolumeCommitmentDto(
            commitment.Id, commitment.ConnectorAccountId, commitment.MonthlyTargetMinor,
            commitment.IsActive, AchievedMinor: 0, GapMinor: commitment.MonthlyTargetMinor,
            DaysLeft: 0);
    }
}

public sealed class UpsertVolumeCommitmentEndpoint(IDispatcher dispatcher)
    : Endpoint<UpsertVolumeCommitmentRequest, VolumeCommitmentDto>
{
    public override void Configure()
    {
        Post("/v1/routing/commitments");
        Description(x => x.WithTags("Routing"));
        Summary(s => s.Summary =
            "Hacim taahhüdü: hesap → aylık hedef ciro. 'commitment' stratejisi açığı olan "
            + "hesabı öne alır; açık kapanınca hesap normal sıraya döner.");
    }

    public override async Task HandleAsync(UpsertVolumeCommitmentRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new UpsertVolumeCommitmentCommand(req.ConnectorAccountId, req.MonthlyTargetMinor), ct), ct);
}

public sealed record ListVolumeCommitmentsQuery : IQuery<IReadOnlyList<VolumeCommitmentDto>>;

/// <summary>
/// Taahhütleri GERÇEKLEŞEN hacimle birlikte döner — hedefi tek başına göstermek işe
/// yaramaz, işyerinin görmek istediği "ne kadar kaldı, kaç gün var".
/// </summary>
public sealed class ListVolumeCommitmentsHandler(RoutingDbContext db, IVolumeProgressSource volumes, IClock clock)
    : IQueryHandler<ListVolumeCommitmentsQuery, IReadOnlyList<VolumeCommitmentDto>>
{
    public async Task<IReadOnlyList<VolumeCommitmentDto>> Handle(
        ListVolumeCommitmentsQuery query, CancellationToken ct)
    {
        var commitments = await db.VolumeCommitments.AsNoTracking()
            .OrderBy(c => c.ConnectorAccountId)
            .ToListAsync(ct);

        if (commitments.Count == 0)
            return [];

        var (periodStart, daysLeft) = RoutingEngine.MonthWindow(clock.UtcNow);
        var achieved = (await volumes.GetAsync(periodStart, ct))
            .ToDictionary(v => v.ConnectorAccountId, v => v.VolumeMinor);

        return commitments.Select(c =>
        {
            var progress = new CommitmentProgress(
                c.MonthlyTargetMinor, achieved.GetValueOrDefault(c.ConnectorAccountId), daysLeft);

            return new VolumeCommitmentDto(
                c.Id, c.ConnectorAccountId, c.MonthlyTargetMinor, c.IsActive,
                progress.AchievedMinor, progress.GapMinor, daysLeft);
        }).ToList();
    }
}

public sealed class ListVolumeCommitmentsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<VolumeCommitmentDto>>
{
    public override void Configure()
    {
        Get("/v1/routing/commitments");
        Description(x => x.WithTags("Routing"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListVolumeCommitmentsQuery(), ct), ct);
}

public sealed record DeactivateVolumeCommitmentCommand(Guid Id)
    : Poyra.SharedKernel.Cqrs.ICommand<bool>;

public sealed class DeactivateVolumeCommitmentHandler(RoutingDbContext db)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<DeactivateVolumeCommitmentCommand, bool>
{
    public async Task<bool> Handle(DeactivateVolumeCommitmentCommand command, CancellationToken ct)
    {
        var commitment = await db.VolumeCommitments.SingleOrDefaultAsync(c => c.Id == command.Id, ct);
        if (commitment is null)
            return false;

        // Silinmez, pasife çekilir: geçmiş rota kararlarının gerekçesi bu kayıtta durur
        commitment.IsActive = false;
        await db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class DeactivateVolumeCommitmentEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/v1/routing/commitments/{id}");
        Description(x => x.WithTags("Routing"));
        Summary(s => s.Summary = "Taahhüdü pasife çeker (kayıt silinmez).");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<Guid>("id");
        if (await dispatcher.Send(new DeactivateVolumeCommitmentCommand(id), ct))
            await Send.NoContentAsync(ct);
        else
            await Send.NotFoundAsync(ct);
    }
}
