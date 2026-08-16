using System.Text.Json;
using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Risk.Domain;
using Poyra.Modules.Risk.Infrastructure;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Rules;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Risk.Features;

public sealed record RiskRuleSetResponse(
    Guid Id, int Version, string Document, bool Active, DateTimeOffset CreatedAt);

public sealed record PublishRiskRulesRequest(string Document);

public sealed record PublishRiskRulesCommand(string Document)
    : Poyra.SharedKernel.Cqrs.ICommand<RiskRuleSetResponse>;

public sealed class PublishRiskRulesValidator : AbstractValidator<PublishRiskRulesCommand>
{
    public PublishRiskRulesValidator()
    {
        RuleFor(x => x.Document).NotEmpty();
        RuleFor(x => x.Document).Must(BeParseable)
            .WithMessage("Risk dokümanı ayrıştırılamadı (geçersiz JSON ya da şema).");
        RuleFor(x => x.Document).Must(HaveValidOutcomes)
            .WithMessage($"Geçersiz sonuç. Geçerli: {string.Join(", ", RiskOutcomes.All)}");
    }

    private static bool BeParseable(string json)
    {
        try
        {
            RiskDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HaveValidOutcomes(string json)
    {
        try
        {
            var document = RiskDocument.Parse(json);
            return RiskOutcomes.All.Contains(document.Default)
                   && document.Rules.All(r => RiskOutcomes.All.Contains(r.Outcome));
        }
        catch (JsonException)
        {
            return true; // ayrıştırma hatası diğer kuralın işi
        }
    }
}

public sealed class PublishRiskRulesHandler(RiskDbContext db, TenantContext tenant, UserContext user)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<PublishRiskRulesCommand, RiskRuleSetResponse>
{
    public async Task<RiskRuleSetResponse> Handle(PublishRiskRulesCommand command, CancellationToken ct)
    {
        var current = await db.RiskRuleSets.SingleOrDefaultAsync(r => r.Active, ct);
        var nextVersion = await db.RiskRuleSets.MaxAsync(r => (int?)r.Version, ct) + 1 ?? 1;

        if (current is not null)
            current.Active = false;

        var published = new RiskRuleSet
        {
            TenantId = tenant.TenantId,
            Version = nextVersion,
            Document = command.Document,
            Active = true,
            PublishedByUserId = user.UserId,
        };

        db.RiskRuleSets.Add(published);
        await db.SaveChangesAsync(ct);

        return new RiskRuleSetResponse(
            published.Id, published.Version, published.Document, true, published.CreatedAt);
    }
}

public sealed class PublishRiskRulesEndpoint(IDispatcher dispatcher)
    : Endpoint<PublishRiskRulesRequest, RiskRuleSetResponse>
{
    public override void Configure()
    {
        Post("/v1/risk/rules");
        Description(x => x.WithTags("Risk"));
        Summary(s => s.Summary =
            "Yeni risk kural seti yayınlar (eskisi pasifleşir, silinmez). "
            + "Koşul dili rota motoruyla aynıdır; sonuç: allow | challenge | review | block.");
    }

    public override async Task HandleAsync(PublishRiskRulesRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new PublishRiskRulesCommand(req.Document), ct), ct);
}

public sealed record ListRiskRuleSetsQuery : IQuery<IReadOnlyList<RiskRuleSetResponse>>;

public sealed class ListRiskRuleSetsHandler(RiskDbContext db)
    : IQueryHandler<ListRiskRuleSetsQuery, IReadOnlyList<RiskRuleSetResponse>>
{
    public async Task<IReadOnlyList<RiskRuleSetResponse>> Handle(
        ListRiskRuleSetsQuery query, CancellationToken ct)
        => await db.RiskRuleSets.AsNoTracking()
            .OrderByDescending(r => r.Version)
            .Select(r => new RiskRuleSetResponse(r.Id, r.Version, r.Document, r.Active, r.CreatedAt))
            .ToListAsync(ct);
}

public sealed class ListRiskRuleSetsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<RiskRuleSetResponse>>
{
    public override void Configure()
    {
        Get("/v1/risk/rules");
        Description(x => x.WithTags("Risk"));
        Summary(s => s.Summary = "Kural seti sürümleri (aktif olan ilk sırada değil, en yeni sürüm üstte).");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListRiskRuleSetsQuery(), ct), ct);
}

public sealed record BlocklistEntryResponse(
    Guid Id, string Kind, string Value, string Reason,
    DateTimeOffset? ExpiresAt, bool Removed, DateTimeOffset CreatedAt);

public sealed record AddBlocklistRequest(
    string Kind, string Value, string Reason, DateTimeOffset? ExpiresAt = null);

public sealed record AddBlocklistCommand(
    string Kind, string Value, string Reason, DateTimeOffset? ExpiresAt)
    : Poyra.SharedKernel.Cqrs.ICommand<BlocklistEntryResponse>;

public sealed class AddBlocklistValidator : AbstractValidator<AddBlocklistCommand>
{
    public AddBlocklistValidator()
    {
        RuleFor(x => x.Kind).Must(BlocklistKindMap.FromDb.ContainsKey)
            .WithMessage($"Geçersiz tür. Geçerli: {string.Join(", ", BlocklistKindMap.FromDb.Keys)}");
        RuleFor(x => x.Value).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500)
            .WithMessage("Gerekçe zorunlu — sebepsiz engel, sonradan kimsenin kaldıramadığı engeldir.");
    }
}

public sealed class AddBlocklistHandler(RiskDbContext db, TenantContext tenant, UserContext user)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<AddBlocklistCommand, BlocklistEntryResponse>
{
    public async Task<BlocklistEntryResponse> Handle(AddBlocklistCommand command, CancellationToken ct)
    {
        var entry = new BlocklistEntry
        {
            TenantId = tenant.TenantId,
            Kind = BlocklistKindMap.FromDb[command.Kind],
            Value = BlocklistEntry.Normalize(command.Value),
            Reason = command.Reason.Trim(),
            ExpiresAt = command.ExpiresAt,
            AddedByUserId = user.UserId,
        };

        db.BlocklistEntries.Add(entry);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new PoyraException(409, "risk.blocklist_duplicate",
                "Bu değer zaten kara listede.");
        }

        return Map(entry);
    }

    internal static BlocklistEntryResponse Map(BlocklistEntry e)
        => new(e.Id, BlocklistKindMap.ToDb[e.Kind], e.Value, e.Reason,
            e.ExpiresAt, e.RemovedAt is not null, e.CreatedAt);
}

public sealed class AddBlocklistEndpoint(IDispatcher dispatcher)
    : Endpoint<AddBlocklistRequest, BlocklistEntryResponse>
{
    public override void Configure()
    {
        Post("/v1/risk/blocklist");
        Description(x => x.WithTags("Risk"));
        Summary(s => s.Summary =
            "Kara listeye ekler. Tür: card (maskeli PAN) | email | ip | customer_ref | bin | country. "
            + "expiresAt ile süreli engel — IP'yi süresiz engellemek masum müşterileri de kaybettirir.");
    }

    public override async Task HandleAsync(AddBlocklistRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new AddBlocklistCommand(req.Kind, req.Value, req.Reason, req.ExpiresAt), ct), ct);
}

public sealed record ListBlocklistQuery(bool IncludeRemoved) : IQuery<IReadOnlyList<BlocklistEntryResponse>>;

public sealed class ListBlocklistHandler(RiskDbContext db)
    : IQueryHandler<ListBlocklistQuery, IReadOnlyList<BlocklistEntryResponse>>
{
    public async Task<IReadOnlyList<BlocklistEntryResponse>> Handle(
        ListBlocklistQuery query, CancellationToken ct)
    {
        var entries = db.BlocklistEntries.AsNoTracking();
        if (!query.IncludeRemoved)
            entries = entries.Where(e => e.RemovedAt == null);

        var rows = await entries.OrderByDescending(e => e.CreatedAt).Take(500).ToListAsync(ct);
        return rows.Select(AddBlocklistHandler.Map).ToList();
    }
}

public sealed class ListBlocklistEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<BlocklistEntryResponse>>
{
    public override void Configure()
    {
        Get("/v1/risk/blocklist");
        Description(x => x.WithTags("Risk"));
        Summary(s => s.Summary = "Kara liste. includeRemoved=true kaldırılmışları da getirir.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListBlocklistQuery(
            Query<bool?>("includeRemoved", isRequired: false) ?? false), ct), ct);
}

public sealed record RemoveBlocklistCommand(Guid EntryId) : Poyra.SharedKernel.Cqrs.ICommand<bool>;

public sealed class RemoveBlocklistHandler(RiskDbContext db, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<RemoveBlocklistCommand, bool>
{
    public async Task<bool> Handle(RemoveBlocklistCommand command, CancellationToken ct)
    {
        var entry = await db.BlocklistEntries.SingleOrDefaultAsync(e => e.Id == command.EntryId, ct)
            ?? throw PoyraException.NotFound("risk.blocklist_not_found", "Kara liste kaydı bulunamadı.");

        if (entry.RemovedAt is null)
        {
            entry.RemovedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }
}

public sealed record RemoveBlocklistResponse(bool Removed);

public sealed class RemoveBlocklistEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<RemoveBlocklistResponse>
{
    public override void Configure()
    {
        Delete("/v1/risk/blocklist/{id}");
        Description(x => x.WithTags("Risk"));
        Summary(s => s.Summary = "Kara listeden çıkarır (kayıt silinmez, kaldırıldı işaretlenir).");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(new RemoveBlocklistResponse(await dispatcher.Send(
            new RemoveBlocklistCommand(Route<Guid>("id")), ct)), ct);
}

public sealed record RiskAssessmentResponse(
    Guid Id, string PaymentId, string Outcome, string? RuleName, string? Reason,
    int? RuleVersion, string Flow, string Signals, DateTimeOffset CreatedAt);

public sealed record ListRiskAssessmentsQuery(string? Outcome, string? PaymentId, int Limit)
    : IQuery<IReadOnlyList<RiskAssessmentResponse>>;

public sealed class ListRiskAssessmentsHandler(RiskDbContext db)
    : IQueryHandler<ListRiskAssessmentsQuery, IReadOnlyList<RiskAssessmentResponse>>
{
    public async Task<IReadOnlyList<RiskAssessmentResponse>> Handle(
        ListRiskAssessmentsQuery query, CancellationToken ct)
    {
        var assessments = db.RiskAssessments.AsNoTracking();

        if (query.Outcome is { Length: > 0 } outcome)
            assessments = assessments.Where(a => a.Outcome == outcome);
        if (query.PaymentId is { Length: > 0 } paymentId)
            assessments = assessments.Where(a => a.PaymentId == paymentId);

        return await assessments
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(query.Limit, 1, 500))
            .Select(a => new RiskAssessmentResponse(
                a.Id, a.PaymentId, a.Outcome, a.RuleName, a.Reason,
                a.RuleVersion, a.Flow, a.Signals, a.CreatedAt))
            .ToListAsync(ct);
    }
}

public sealed class ListRiskAssessmentsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<RiskAssessmentResponse>>
{
    public override void Configure()
    {
        Get("/v1/risk/assessments");
        Description(x => x.WithTags("Risk"));
        Summary(s => s.Summary =
            "Verilen risk kararları (silinemez). Filtreler: outcome, paymentId, limit.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListRiskAssessmentsQuery(
            Query<string?>("outcome", isRequired: false),
            Query<string?>("paymentId", isRequired: false),
            Query<int?>("limit", isRequired: false) ?? 100), ct), ct);
}

public sealed record TestRiskRulesRequest(
    string Document,
    long AmountMinor,
    string Currency = "TRY",
    int Installments = 1,
    string Flow = "hosted",
    int? Hour = null,
    string? Bin = null,
    string? Program = null,
    string? Brand = null,
    string? CustomerRef = null,
    string? Ip = null,
    int Attempts1h = 0,
    int Declines1h = 0,
    int DistinctCards24h = 0);

public sealed record TestRiskRulesResponse(
    string Outcome, string? RuleName, string? Reason, IReadOnlyDictionary<string, object?> Signals);

public sealed record TestRiskRulesCommand(TestRiskRulesRequest Input)
    : Poyra.SharedKernel.Cqrs.ICommand<TestRiskRulesResponse>;

public sealed class TestRiskRulesHandler
    : Poyra.SharedKernel.Cqrs.ICommandHandler<TestRiskRulesCommand, TestRiskRulesResponse>
{
    public Task<TestRiskRulesResponse> Handle(TestRiskRulesCommand command, CancellationToken ct)
    {
        var input = command.Input;

        RiskDocument document;
        try
        {
            document = RiskDocument.Parse(input.Document);
        }
        catch (JsonException ex)
        {
            throw new PoyraException(400, "risk.invalid_document", $"Doküman ayrıştırılamadı: {ex.Message}");
        }

        var facts = RiskEngine.BuildFacts(
            new RiskContext("pay_test", input.AmountMinor, input.Currency, input.Installments,
                input.Flow, input.CustomerRef, input.Ip, null, input.Bin, null,
                input.Program, input.Brand, null, null, null),
            new VelocitySnapshot(input.Attempts1h, input.Attempts1h, input.Declines1h, 0, input.DistinctCards24h),
            blocklistHit: null,
            turkeyHour: input.Hour ?? RiskEngine.TurkeyHour(DateTimeOffset.UtcNow));

        var match = document.Rules.FirstOrDefault(r => r.When is null || RuleEngine.Evaluate(r.When, facts));

        return Task.FromResult(match is null
            ? new TestRiskRulesResponse(document.Default, null, "Hiçbir kural eşleşmedi.", facts.Snapshot())
            : new TestRiskRulesResponse(match.Outcome, match.Name, match.Reason, facts.Snapshot()));
    }
}

public sealed class TestRiskRulesEndpoint(IDispatcher dispatcher)
    : Endpoint<TestRiskRulesRequest, TestRiskRulesResponse>
{
    public override void Configure()
    {
        Post("/v1/risk/test");
        Description(x => x.WithTags("Risk"));
        Summary(s => s.Summary =
            "Aday kuralı verilen sinyallerle dener (salt okur, kayıt açmaz). "
            + "Yayınlamadan önce hangi işlemin engelleneceğini gösterir.");
    }

    public override async Task HandleAsync(TestRiskRulesRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new TestRiskRulesCommand(req), ct), ct);
}
