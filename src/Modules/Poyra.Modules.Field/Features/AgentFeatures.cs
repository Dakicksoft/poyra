using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Field.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Field.Features;

public sealed record FieldAgentDto(
    Guid Id,
    string Code,
    string Name,
    string? Region,
    string? DeviceId,
    bool PinSet,
    DateTimeOffset? EnrolledAt,
    DateTimeOffset? LastSyncAt,
    bool Active,
    string? DisabledReason);

internal static class FieldAgentMap
{
    public static FieldAgentDto ToDto(FieldAgent a) => new(
        a.Id, a.Code, a.Name, a.Region, a.DeviceId,
        !string.IsNullOrEmpty(a.PinHash), a.EnrolledAt, a.LastSyncAt,
        a.IsActive, a.DisabledReason);
}


public sealed record CreateFieldAgentRequest(string Code, string Name, string? Region, Guid? UserId);

public sealed record CreateFieldAgentCommand(string Code, string Name, string? Region, Guid? UserId)
    : Poyra.SharedKernel.Cqrs.ICommand<FieldAgentDto>;

public sealed class CreateFieldAgentValidator : AbstractValidator<CreateFieldAgentCommand>
{
    public CreateFieldAgentValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Region).MaximumLength(100);
    }
}

public sealed class CreateFieldAgentHandler(FieldDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreateFieldAgentCommand, FieldAgentDto>
{
    public async Task<FieldAgentDto> Handle(CreateFieldAgentCommand command, CancellationToken ct)
    {
        var code = TurkishText.Fold(command.Code);

        if (await db.FieldAgents.AnyAsync(a => a.Code == code, ct))
            throw PoyraException.Conflict("field.agent_code_conflict",
                $"Bu temsilci kodu zaten kullanılıyor: {code}.");

        var agent = new FieldAgent
        {
            TenantId = tenant.TenantId,
            Code = code,
            Name = command.Name,
            Region = command.Region,
            UserId = command.UserId,
        };

        db.FieldAgents.Add(agent);
        await db.SaveChangesAsync(ct);
        return FieldAgentMap.ToDto(agent);
    }
}

public sealed class CreateFieldAgentEndpoint(IDispatcher dispatcher)
    : Endpoint<CreateFieldAgentRequest, FieldAgentDto>
{
    public override void Configure()
    {
        Post("/v1/field/agents");
        Description(x => x.WithTags("Field"));
        Summary(s => s.Summary = "Saha temsilcisi tanımlar. Kod işyeri içinde tekildir.");
    }

    public override async Task HandleAsync(CreateFieldAgentRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new CreateFieldAgentCommand(req.Code, req.Name, req.Region, req.UserId), ct), ct);
}


public sealed record ListFieldAgentsQuery : IQuery<IReadOnlyList<FieldAgentDto>>;

public sealed class ListFieldAgentsHandler(FieldDbContext db)
    : IQueryHandler<ListFieldAgentsQuery, IReadOnlyList<FieldAgentDto>>
{
    public async Task<IReadOnlyList<FieldAgentDto>> Handle(ListFieldAgentsQuery query, CancellationToken ct)
        => (await db.FieldAgents.AsNoTracking()
                .OrderBy(a => a.Code)
                .ToListAsync(ct))
            .Select(FieldAgentMap.ToDto)
            .ToList();
}

public sealed class ListFieldAgentsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<FieldAgentDto>>
{
    public override void Configure()
    {
        Get("/v1/field/agents");
        Description(x => x.WithTags("Field"));
        Summary(s => s.Summary = "Saha temsilcileri (kapatılmışlar dahil — kayıt silinmez).");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListFieldAgentsQuery(), ct), ct);
}


public sealed record ReleaseDeviceRequest(string Reason);

public sealed record ReleaseDeviceCommand(Guid AgentId, string Reason) : Poyra.SharedKernel.Cqrs.ICommand<FieldAgentDto>;

public sealed class ReleaseDeviceValidator : AbstractValidator<ReleaseDeviceCommand>
{
    public ReleaseDeviceValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
}

public sealed class ReleaseDeviceHandler(FieldDbContext db, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ReleaseDeviceCommand, FieldAgentDto>
{
    public async Task<FieldAgentDto> Handle(ReleaseDeviceCommand command, CancellationToken ct)
    {
        var agent = await db.FieldAgents.SingleOrDefaultAsync(a => a.Id == command.AgentId, ct)
            ?? throw PoyraException.NotFound("field.agent_not_found", "Saha temsilcisi bulunamadı.");

        agent.DeviceId = null;
        agent.EnrolledAt = null;
        agent.PinHash = null; // yeni cihazda PIN yeniden kurulur
        agent.UpdatedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);
        return FieldAgentMap.ToDto(agent);
    }
}

public sealed class ReleaseDeviceEndpoint(IDispatcher dispatcher)
    : Endpoint<ReleaseDeviceRequest, FieldAgentDto>
{
    public override void Configure()
    {
        Post("/v1/field/agents/{id}/release-device");
        Description(x => x.WithTags("Field"));
        Summary(s => s.Summary =
            "Cihaz kaydını serbest bırakır (telefon değişimi/kaybı). Sonraki senkron yeni cihazı bağlar.");
    }

    public override async Task HandleAsync(ReleaseDeviceRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new ReleaseDeviceCommand(Route<Guid>("id"), req.Reason), ct), ct);
}


public sealed record DisableAgentRequest(string Reason);

public sealed record DisableAgentCommand(Guid AgentId, string Reason) : Poyra.SharedKernel.Cqrs.ICommand<FieldAgentDto>;

public sealed class DisableAgentValidator : AbstractValidator<DisableAgentCommand>
{
    public DisableAgentValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
}

public sealed class DisableAgentHandler(FieldDbContext db, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<DisableAgentCommand, FieldAgentDto>
{
    public async Task<FieldAgentDto> Handle(DisableAgentCommand command, CancellationToken ct)
    {
        var agent = await db.FieldAgents.SingleOrDefaultAsync(a => a.Id == command.AgentId, ct)
            ?? throw PoyraException.NotFound("field.agent_not_found", "Saha temsilcisi bulunamadı.");

        agent.DisabledAt = clock.UtcNow;
        agent.DisabledReason = command.Reason;
        agent.UpdatedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);
        return FieldAgentMap.ToDto(agent);
    }
}

public sealed class DisableAgentEndpoint(IDispatcher dispatcher)
    : Endpoint<DisableAgentRequest, FieldAgentDto>
{
    public override void Configure()
    {
        Post("/v1/field/agents/{id}/disable");
        Description(x => x.WithTags("Field"));
        Summary(s => s.Summary =
            "Temsilciyi kapatır (silmez). Kapatılan temsilcinin senkronu reddedilir, geçmişi kalır.");
    }

    public override async Task HandleAsync(DisableAgentRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new DisableAgentCommand(Route<Guid>("id"), req.Reason), ct), ct);
}
