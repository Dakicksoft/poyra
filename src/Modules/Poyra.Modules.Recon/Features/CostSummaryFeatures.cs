using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Recon.Features;

public sealed record CostByInstallmentRow(
    int Installments, int Count, long GrossMinor, long CommissionMinor, int EffectiveRateBps, long DeltaMinor);

public sealed record CostSummaryDto(
    int Days,
    DateOnly FromDay,
    DateOnly ToDay,
    int AuditedCount,
    int UnauditedCount,
    long GrossMinor,
    long CommissionMinor,
    int EffectiveRateBps,
    long ExpectedCommissionMinor,
    long DeltaMinor,
    IReadOnlyList<CostByInstallmentRow> ByInstallment);

public sealed record CostSummaryQuery(int Days) : IQuery<CostSummaryDto>;

public sealed class CostSummaryHandler(ReconDbContext db, IClock clock)
    : IQueryHandler<CostSummaryQuery, CostSummaryDto>
{
    public async Task<CostSummaryDto> Handle(CostSummaryQuery query, CancellationToken ct)
    {
        var days = Math.Clamp(query.Days, 1, 365);
        var toDay = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime.AddHours(3));
        var fromDay = toDay.AddDays(-(days - 1));

        var statementIds = await db.ReconStatements.AsNoTracking()
            .Where(s => s.StatementDate >= fromDay && s.StatementDate <= toDay)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var findings = await db.CommissionAuditFindings.AsNoTracking()
            .Where(f => statementIds.Contains(f.StatementId))
            .Select(f => new
            {
                f.Installments,
                f.GrossMinor,
                f.ActualCommissionMinor,
                f.ExpectedCommissionMinor,
                f.DeltaMinor,
            })
            .ToListAsync(ct);

        var audited = findings.Where(f => f.ExpectedCommissionMinor.HasValue).ToList();
        var gross = findings.Sum(f => f.GrossMinor);
        var commission = findings.Sum(f => f.ActualCommissionMinor);

        var byInstallment = findings
            .GroupBy(f => f.Installments)
            .Select(g =>
            {
                var groupGross = g.Sum(f => f.GrossMinor);
                var groupCommission = g.Sum(f => f.ActualCommissionMinor);
                return new CostByInstallmentRow(
                    g.Key, g.Count(), groupGross, groupCommission,
                    Bps(groupCommission, groupGross),
                    g.Sum(f => f.DeltaMinor ?? 0));
            })
            .OrderBy(r => r.Installments)
            .ToList();

        return new CostSummaryDto(
            days, fromDay, toDay,
            audited.Count,
            findings.Count - audited.Count,
            gross,
            commission,
            Bps(commission, gross),
            audited.Sum(f => f.ExpectedCommissionMinor ?? 0),
            audited.Sum(f => f.DeltaMinor ?? 0),
            byInstallment);
    }

    private static int Bps(long commission, long gross)
        => gross == 0 ? 0 : (int)Math.Round(commission * 10_000m / gross);
}

public sealed class CostSummaryEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<CostSummaryDto>
{
    public override void Configure()
    {
        Get("/v1/recon/cost-summary");
        Description(x => x.WithTags("Recon"));
        Summary(s => s.Summary =
            "Dönemin GERÇEK komisyon maliyeti (bankanın kestiği) ve anlaşmaya göre farkı.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(
            new CostSummaryQuery(Query<int?>("days", isRequired: false) ?? 30), ct), ct);
}
