using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Recon.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Recon.Features;

public sealed record CommissionClaimDto(
    string Id,
    Guid ConnectorAccountId,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    long ClaimedMinor,
    long RecoveredMinor,
    long OutstandingMinor,
    int FindingCount,
    string Status,
    string? BankReference,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? RespondedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset CreatedAt);

public sealed record ClaimEventDto(string EventType, string? Note, long? AmountMinor, DateTimeOffset At);

internal static class ClaimMap
{
    public static CommissionClaimDto ToDto(CommissionClaim c) => new(
        c.PublicId, c.ConnectorAccountId, c.PeriodFrom, c.PeriodTo,
        c.ClaimedMinor, c.RecoveredMinor, c.OutstandingMinor, c.FindingCount,
        ClaimStatusMap.ToDb[c.Status], c.BankReference,
        c.SubmittedAt, c.RespondedAt, c.ClosedAt, c.CreatedAt);
}


public sealed record OpenClaimRequest(Guid ConnectorAccountId, DateOnly PeriodFrom, DateOnly PeriodTo);

public sealed record OpenClaimCommand(Guid ConnectorAccountId, DateOnly PeriodFrom, DateOnly PeriodTo)
    : Poyra.SharedKernel.Cqrs.ICommand<CommissionClaimDto>;

public sealed class OpenClaimValidator : AbstractValidator<OpenClaimCommand>
{
    public OpenClaimValidator()
        => RuleFor(x => x.PeriodTo).GreaterThanOrEqualTo(x => x.PeriodFrom)
            .WithMessage("Dönem bitişi başlangıçtan önce olamaz.");
}

public sealed class OpenClaimHandler(ReconDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<OpenClaimCommand, CommissionClaimDto>
{
    public async Task<CommissionClaimDto> Handle(OpenClaimCommand command, CancellationToken ct)
    {
        var periodStart = TurkeyTime.StartOfDay(command.PeriodFrom);
        var periodEnd = TurkeyTime.EndOfDay(command.PeriodTo);

        var findings = await (
                from finding in db.CommissionAuditFindings.AsNoTracking()
                join statement in db.ReconStatements.AsNoTracking()
                    on finding.StatementId equals statement.Id
                where statement.ConnectorAccountId == command.ConnectorAccountId
                      && finding.CreatedAt >= periodStart && finding.CreatedAt < periodEnd
                      && finding.DeltaMinor != null && finding.DeltaMinor > 0
                select finding.DeltaMinor!.Value)
            .ToListAsync(ct);

        if (findings.Count == 0)
            throw PoyraException.Conflict("claim.no_findings",
                "Bu dönemde fazla kesim bulgusu yok — talep edilecek tutar bulunamadı.");

        var claim = new CommissionClaim
        {
            TenantId = tenant.TenantId,
            ConnectorAccountId = command.ConnectorAccountId,
            PeriodFrom = command.PeriodFrom,
            PeriodTo = command.PeriodTo,
            ClaimedMinor = findings.Sum(),
            FindingCount = findings.Count,
        };

        db.CommissionClaims.Add(claim);
        await db.SaveChangesAsync(ct);

        return ClaimMap.ToDto(claim);
    }
}

public sealed class OpenClaimEndpoint(IDispatcher dispatcher)
    : Endpoint<OpenClaimRequest, CommissionClaimDto>
{
    public override void Configure()
    {
        Post("/v1/recon/claims");
        Description(x => x.WithTags("Recon"));
        Summary(s =>
        {
            s.Summary = "Komisyon itirazı açar — dönemdeki fazla kesimleri tek talepte toplar.";
            s.Description =
                "Yalnız **fazla kesim** (delta > 0) talep edilir. Eksik kesilenler bankanın "
                + "lehine hatadır ve talebe girmez — girseydi toplam yanlış çıkar ve bankaya "
                + "\"bize eksik kestiniz\" demiş olurduk.";
        });
    }

    public override async Task HandleAsync(OpenClaimRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new OpenClaimCommand(req.ConnectorAccountId, req.PeriodFrom, req.PeriodTo), ct), ct);
}


public sealed record AdvanceClaimRequest(string Action, string? Note, long? AmountMinor, string? BankReference);

public sealed record AdvanceClaimCommand(
    string ClaimId, string Action, string? Note, long? AmountMinor, string? BankReference)
    : Poyra.SharedKernel.Cqrs.ICommand<CommissionClaimDto>;

public sealed class AdvanceClaimValidator : AbstractValidator<AdvanceClaimCommand>
{
    public static readonly string[] Actions = ["submit", "respond", "recover", "reject", "close", "note"];

    public AdvanceClaimValidator()
    {
        RuleFor(x => x.Action).Must(a => Actions.Contains(a))
            .WithMessage($"Geçerli eylemler: {string.Join(", ", Actions)}");
        RuleFor(x => x.AmountMinor).GreaterThan(0)
            .When(x => x.Action == "recover")
            .WithMessage("Tahsilat kaydı için tutar zorunludur.");
    }
}

public sealed class AdvanceClaimHandler(
    ReconDbContext db, TenantContext tenant, UserContext user, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<AdvanceClaimCommand, CommissionClaimDto>
{
    public async Task<CommissionClaimDto> Handle(AdvanceClaimCommand command, CancellationToken ct)
    {
        var claim = await db.CommissionClaims
            .SingleOrDefaultAsync(c => c.PublicId == command.ClaimId, ct)
            ?? throw PoyraException.NotFound("claim.not_found", "İtiraz kaydı bulunamadı.");

        var now = clock.UtcNow;

        switch (command.Action)
        {
            case "submit":
                claim.Status = ClaimStatus.Submitted;
                claim.SubmittedAt = now;
                claim.BankReference = command.BankReference ?? claim.BankReference;
                break;

            case "respond":
                claim.Status = ClaimStatus.Accepted;
                claim.RespondedAt = now;
                claim.BankReference = command.BankReference ?? claim.BankReference;
                break;

            case "recover":
                claim.RecoveredMinor += command.AmountMinor!.Value;
                claim.Status = CommissionClaim.StatusAfterRecovery(claim.ClaimedMinor, claim.RecoveredMinor);
                if (claim.Status == ClaimStatus.Closed)
                    claim.ClosedAt = now;
                break;

            case "reject":
                claim.Status = ClaimStatus.Rejected;
                claim.RespondedAt = now;
                claim.ClosedAt = now;
                break;

            case "close":
                claim.Status = ClaimStatus.Closed;
                claim.ClosedAt = now;
                break;
        }

        claim.UpdatedAt = now;

        db.CommissionClaimEvents.Add(new CommissionClaimEvent
        {
            TenantId = tenant.TenantId,
            ClaimId = claim.Id,
            EventType = command.Action,
            Note = command.Note,
            AmountMinor = command.AmountMinor,
            ActorUserId = user.UserId,
        });

        await db.SaveChangesAsync(ct);
        return ClaimMap.ToDto(claim);
    }
}

public sealed class AdvanceClaimEndpoint(IDispatcher dispatcher)
    : Endpoint<AdvanceClaimRequest, CommissionClaimDto>
{
    public override void Configure()
    {
        Post("/v1/recon/claims/{id}/advance");
        Description(x => x.WithTags("Recon"));
        Summary(s =>
        {
            s.Summary = "İtirazı ilerletir: submit · respond · recover · reject · close · note.";
            s.Description =
                "Her adım **değiştirilemez** bir zaman çizelgesi kaydı bırakır. "
                + "\"Ne zaman ilettik, banka ne dedi, para ne zaman geldi\" sorusu aylar sonra "
                + "sorulur ve o zaman kimse hatırlamaz.\n\n"
                + "`recover` kısmi olabilir ve **birikir** — banka parayı parça parça iade "
                + "edebilir. Tamamı gelene kadar itiraz **kapanmaz**.";
        });
    }

    public override async Task HandleAsync(AdvanceClaimRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new AdvanceClaimCommand(
            Route<string>("id")!, req.Action, req.Note, req.AmountMinor, req.BankReference), ct), ct);
}

public sealed record ClaimSummary(
    long OpenClaimedMinor,
    long RecoveredThisYearMinor,
    int OpenCount,
    int ClosedCount,
    IReadOnlyList<CommissionClaimDto> Claims);

public sealed record ListClaimsQuery : IQuery<ClaimSummary>;

public sealed class ListClaimsHandler(ReconDbContext db, IClock clock)
    : IQueryHandler<ListClaimsQuery, ClaimSummary>
{
    public async Task<ClaimSummary> Handle(ListClaimsQuery query, CancellationToken ct)
    {
        var claims = await db.CommissionClaims.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        var yearStart = TurkeyTime.StartOfDay(new DateOnly(TurkeyTime.DayOf(clock.UtcNow).Year, 1, 1));

        return new ClaimSummary(
            claims.Where(c => c.Status is not (ClaimStatus.Closed or ClaimStatus.Rejected))
                .Sum(c => c.OutstandingMinor),
            claims.Where(c => c.ClosedAt >= yearStart || c.UpdatedAt >= yearStart)
                .Sum(c => c.RecoveredMinor),
            claims.Count(c => c.Status is not (ClaimStatus.Closed or ClaimStatus.Rejected)),
            claims.Count(c => c.Status is ClaimStatus.Closed or ClaimStatus.Rejected),
            claims.Select(ClaimMap.ToDto).ToList());
    }
}

public sealed class ListClaimsEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<ClaimSummary>
{
    public override void Configure()
    {
        Get("/v1/recon/claims");
        Description(x => x.WithTags("Recon"));
        Summary(s => s.Summary =
            "Komisyon itirazları ve tahsilat özeti — \"bu yıl bankalardan geri alınan\".");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListClaimsQuery(), ct), ct);
}

public sealed record ClaimTimelineQuery(string ClaimId) : IQuery<IReadOnlyList<ClaimEventDto>>;

public sealed class ClaimTimelineHandler(ReconDbContext db)
    : IQueryHandler<ClaimTimelineQuery, IReadOnlyList<ClaimEventDto>>
{
    public async Task<IReadOnlyList<ClaimEventDto>> Handle(
        ClaimTimelineQuery query, CancellationToken ct)
        => await (
                from evt in db.CommissionClaimEvents.AsNoTracking()
                join claim in db.CommissionClaims.AsNoTracking() on evt.ClaimId equals claim.Id
                where claim.PublicId == query.ClaimId
                orderby evt.CreatedAt
                select new ClaimEventDto(evt.EventType, evt.Note, evt.AmountMinor, evt.CreatedAt))
            .ToListAsync(ct);
}

public sealed class ClaimTimelineEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<ClaimEventDto>>
{
    public override void Configure()
    {
        Get("/v1/recon/claims/{id}/timeline");
        Description(x => x.WithTags("Recon"));
        Summary(s => s.Summary = "İtirazın değiştirilemez zaman çizelgesi.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(
            new ClaimTimelineQuery(Route<string>("id")!), ct), ct);
}
