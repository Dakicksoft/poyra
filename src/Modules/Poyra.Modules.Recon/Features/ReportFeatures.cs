using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Recon.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Recon.Features;

public sealed record ListStatementsQuery : IQuery<IReadOnlyList<StatementSummaryDto>>;

public sealed class ListStatementsHandler(ReconDbContext db)
    : IQueryHandler<ListStatementsQuery, IReadOnlyList<StatementSummaryDto>>
{
    public async Task<IReadOnlyList<StatementSummaryDto>> Handle(ListStatementsQuery query, CancellationToken ct)
    {
        var statements = await db.ReconStatements.AsNoTracking()
            .OrderByDescending(s => s.StatementDate).ThenByDescending(s => s.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        var ids = statements.Select(s => s.Id).ToArray();
        var totals = await db.CommissionAuditFindings.AsNoTracking()
            .Where(f => ids.Contains(f.StatementId))
            .GroupBy(f => f.StatementId)
            .Select(g => new
            {
                StatementId = g.Key,
                Expected = g.Sum(f => f.ExpectedCommissionMinor ?? 0),
                Actual = g.Sum(f => f.ExpectedCommissionMinor != null ? f.ActualCommissionMinor : 0),
                Delta = g.Sum(f => f.DeltaMinor ?? 0),
                AgreementMissing = g.Count(f => f.ExpectedCommissionMinor == null),
            })
            .ToDictionaryAsync(x => x.StatementId, ct);

        return statements.Select(s =>
        {
            var t = totals.GetValueOrDefault(s.Id);
            return new StatementSummaryDto(
                s.Id, s.ConnectorAccountId, s.StatementDate, s.Status.ToString().ToLowerInvariant(),
                s.LineCount, s.RefundLineCount, s.MatchedCount, s.MissingInPoyraCount,
                s.AmountMismatchCount, s.MissingInStatementCount,
                t?.Expected ?? 0, t?.Actual ?? 0, t?.Delta ?? 0, t?.AgreementMissing ?? 0,
                s.RefundCommissionSum);
        }).ToList();
    }
}

public sealed class ListStatementsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<StatementSummaryDto>>
{
    public override void Configure()
    {
        Get("/v1/recon/statements");
        Description(x => x.WithTags("Recon"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListStatementsQuery(), ct), ct);
}

public sealed record ExceptionLineDto(
    int LineNo, string OrderId, string LineType, long GrossMinor, string MatchStatus);

public sealed record StatementExceptionsDto(
    Guid StatementId,
    IReadOnlyList<ExceptionLineDto> MissingInPoyra, // ekstrede var, defterde yok
    IReadOnlyList<ExceptionLineDto> AmountMismatch,
    int MissingInStatementCount); // defterde var, ekstrede yok

public sealed record GetExceptionsQuery(Guid StatementId) : IQuery<StatementExceptionsDto?>;

public sealed class GetExceptionsHandler(ReconDbContext db)
    : IQueryHandler<GetExceptionsQuery, StatementExceptionsDto?>
{
    public async Task<StatementExceptionsDto?> Handle(GetExceptionsQuery query, CancellationToken ct)
    {
        var statement = await db.ReconStatements.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == query.StatementId, ct);
        if (statement is null)
            return null;

        var problemLines = await db.ReconStatementLines.AsNoTracking()
            .Where(l => l.StatementId == statement.Id && l.MatchStatus != LineMatchStatus.Matched)
            .OrderBy(l => l.LineNo)
            .ToListAsync(ct);

        static ExceptionLineDto ToDto(ReconStatementLine l, string status)
            => new(l.LineNo, l.OrderId,
                l.LineType == StatementLineType.Refund ? "refund" : "sale", l.GrossMinor, status);

        return new StatementExceptionsDto(
            statement.Id,
            problemLines.Where(l => l.MatchStatus == LineMatchStatus.MissingInPoyra)
                .Select(l => ToDto(l, "missing_in_poyra")).ToList(),
            problemLines.Where(l => l.MatchStatus == LineMatchStatus.AmountMismatch)
                .Select(l => ToDto(l, "amount_mismatch")).ToList(),
            statement.MissingInStatementCount);
    }
}

public sealed class GetExceptionsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<StatementExceptionsDto>
{
    public override void Configure()
    {
        Get("/v1/recon/statements/{id}/exceptions");
        Description(x => x.WithTags("Recon"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await dispatcher.Ask(new GetExceptionsQuery(Route<Guid>("id")), ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(result, ct);
    }
}

public sealed record CommissionFindingDto(
    string OrderId,
    int Installments,
    long GrossMinor,
    int? AgreedRateBps,
    long? ExpectedCommissionMinor,
    long ActualCommissionMinor,
    long? DeltaMinor);

public sealed record CommissionReportDto(
    Guid StatementId,
    DateOnly StatementDate,
    long ExpectedCommissionSum,
    long ActualCommissionSum,
    long DeltaSum, // > 0: banka FAZLA kesmiş — itiraz tutarı
    int OverchargedCount,
    int UnderchargedCount,
    int AgreementMissingCount,
    IReadOnlyList<CommissionFindingDto> Discrepancies); // yalnız delta != 0 satırları

public sealed record GetCommissionReportQuery(Guid StatementId) : IQuery<CommissionReportDto?>;

public sealed class GetCommissionReportHandler(ReconDbContext db)
    : IQueryHandler<GetCommissionReportQuery, CommissionReportDto?>
{
    public async Task<CommissionReportDto?> Handle(GetCommissionReportQuery query, CancellationToken ct)
    {
        var statement = await db.ReconStatements.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == query.StatementId, ct);
        if (statement is null)
            return null;

        var findings = await (
                from finding in db.CommissionAuditFindings.AsNoTracking()
                join line in db.ReconStatementLines.AsNoTracking() on finding.StatementLineId equals line.Id
                where finding.StatementId == statement.Id
                select new { finding, line.OrderId })
            .ToListAsync(ct);

        var audited = findings.Where(f => f.finding.ExpectedCommissionMinor != null).ToList();

        return new CommissionReportDto(
            statement.Id,
            statement.StatementDate,
            audited.Sum(f => f.finding.ExpectedCommissionMinor!.Value),
            audited.Sum(f => f.finding.ActualCommissionMinor),
            audited.Sum(f => f.finding.DeltaMinor!.Value),
            audited.Count(f => f.finding.DeltaMinor > 0),
            audited.Count(f => f.finding.DeltaMinor < 0),
            findings.Count(f => f.finding.ExpectedCommissionMinor == null),
            findings
                .Where(f => f.finding.DeltaMinor is not null and not 0)
                .OrderByDescending(f => Math.Abs(f.finding.DeltaMinor!.Value))
                .Select(f => new CommissionFindingDto(
                    f.OrderId, f.finding.Installments, f.finding.GrossMinor,
                    f.finding.AgreedRateBps, f.finding.ExpectedCommissionMinor,
                    f.finding.ActualCommissionMinor, f.finding.DeltaMinor))
                .ToList());
    }
}

public sealed class GetCommissionReportEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<CommissionReportDto>
{
    public override void Configure()
    {
        Get("/v1/recon/statements/{id}/commission-report");
        Description(x => x.WithTags("Recon"));
        Summary(s => s.Summary = "Komisyon denetimi: beklenen ↔ kesilen; delta>0 satırlar bankaya itiraz konusudur.");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await dispatcher.Ask(new GetCommissionReportQuery(Route<Guid>("id")), ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(result, ct);
    }
}

public sealed record ValorLineDto(
    string OrderId, DateOnly? ValueDate, DateOnly ExpectedValueDate, int DeltaDays);

public sealed record ValorReportDto(
    Guid StatementId,
    DateOnly StatementDate,
    int AuditedCount, // beklenen + gerçekleşen valörü olan satırlar
    int OnTimeCount,
    int LateCount, // banka GEÇ ödemiş (delta > 0) — itiraz/faiz konusu
    int EarlyCount,
    int TotalLateDays,
    IReadOnlyList<ValorLineDto> LateLines);

public sealed record GetValorReportQuery(Guid StatementId) : IQuery<ValorReportDto?>;

public sealed class GetValorReportHandler(ReconDbContext db)
    : IQueryHandler<GetValorReportQuery, ValorReportDto?>
{
    public async Task<ValorReportDto?> Handle(GetValorReportQuery query, CancellationToken ct)
    {
        var statement = await db.ReconStatements.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == query.StatementId, ct);
        if (statement is null)
            return null;

        var audited = await db.ReconStatementLines.AsNoTracking()
            .Where(l => l.StatementId == statement.Id && l.ValorDeltaDays != null)
            .OrderByDescending(l => l.ValorDeltaDays)
            .ToListAsync(ct);

        return new ValorReportDto(
            statement.Id,
            statement.StatementDate,
            audited.Count,
            audited.Count(l => l.ValorDeltaDays == 0),
            audited.Count(l => l.ValorDeltaDays > 0),
            audited.Count(l => l.ValorDeltaDays < 0),
            audited.Where(l => l.ValorDeltaDays > 0).Sum(l => l.ValorDeltaDays!.Value),
            audited.Where(l => l.ValorDeltaDays > 0)
                .Select(l => new ValorLineDto(
                    l.OrderId, l.ValueDate, l.ExpectedValueDate!.Value, l.ValorDeltaDays!.Value))
                .ToList());
    }
}

public sealed class GetValorReportEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<ValorReportDto>
{
    public override void Configure()
    {
        Get("/v1/recon/statements/{id}/valor-report");
        Description(x => x.WithTags("Recon"));
        Summary(s => s.Summary = "Valör denetimi: anlaşılan hesaba geçiş ↔ bankanın beyanı; geç günler itiraz konusudur.");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await dispatcher.Ask(new GetValorReportQuery(Route<Guid>("id")), ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(result, ct);
    }
}
