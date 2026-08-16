using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Recon.Domain;
using Poyra.Modules.Recon.Infrastructure;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Recon.Features;

public sealed record StatementLineInput(
    string OrderId,
    long GrossMinor,
    long CommissionMinor,
    long NetMinor,
    DateOnly? ValueDate = null,
    string LineType = "sale");

public sealed record StatementSummaryDto(
    Guid Id,
    Guid ConnectorAccountId,
    DateOnly StatementDate,
    string Status, // imported | matching | matched
    int LineCount,
    int RefundLineCount,
    int MatchedCount,
    int MissingInPoyraCount,
    int AmountMismatchCount,
    int MissingInStatementCount,
    long ExpectedCommissionSum,
    long ActualCommissionSum,
    long CommissionDeltaSum,
    int AgreementMissingCount,
    long RefundCommissionSum);

internal static class StatementSummaries
{
    public static async Task<StatementSummaryDto> BuildAsync(
        ReconDbContext db, ReconStatement statement, CancellationToken ct)
    {
        var totals = await db.CommissionAuditFindings.AsNoTracking()
            .Where(f => f.StatementId == statement.Id)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Expected = g.Sum(f => f.ExpectedCommissionMinor ?? 0),
                Actual = g.Sum(f => f.ExpectedCommissionMinor != null ? f.ActualCommissionMinor : 0),
                Delta = g.Sum(f => f.DeltaMinor ?? 0),
                AgreementMissing = g.Count(f => f.ExpectedCommissionMinor == null),
            })
            .SingleOrDefaultAsync(ct);

        return new StatementSummaryDto(
            statement.Id, statement.ConnectorAccountId, statement.StatementDate,
            statement.Status.ToString().ToLowerInvariant(),
            statement.LineCount, statement.RefundLineCount, statement.MatchedCount,
            statement.MissingInPoyraCount, statement.AmountMismatchCount,
            statement.MissingInStatementCount,
            totals?.Expected ?? 0, totals?.Actual ?? 0, totals?.Delta ?? 0,
            totals?.AgreementMissing ?? 0, statement.RefundCommissionSum);
    }
}

public sealed record ImportStatementRequest(
    Guid ConnectorAccountId, DateOnly StatementDate, List<StatementLineInput> Lines);

public sealed record ImportStatementCommand(
    Guid ConnectorAccountId, DateOnly StatementDate, List<StatementLineInput> Lines)
    : Poyra.SharedKernel.Cqrs.ICommand<StatementSummaryDto>;

public sealed class ImportStatementValidator : AbstractValidator<ImportStatementCommand>
{
    public ImportStatementValidator()
    {
        RuleFor(x => x.Lines).NotEmpty().Must(l => l.Count <= 50_000)
            .WithMessage("Tek ekstrede en fazla 50.000 satır.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.OrderId).NotEmpty().MaximumLength(64);
            line.RuleFor(l => l.GrossMinor).GreaterThan(0);
            line.RuleFor(l => l.CommissionMinor).GreaterThanOrEqualTo(0);
            line.RuleFor(l => l.LineType).Must(t => t is "sale" or "refund")
                .WithMessage("lineType 'sale' veya 'refund' olmalı.");
        });
    }
}

public sealed class ImportStatementHandler(
    ReconDbContext db,
    TenantContext tenant,
    StatementMatcher matcher,
    IConnectorAccountsDirectory accounts,
    IBackgroundJobClient jobs,
    IConfiguration configuration)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ImportStatementCommand, StatementSummaryDto>
{
    public async Task<StatementSummaryDto> Handle(ImportStatementCommand command, CancellationToken ct)
    {
        var knownAccounts = await accounts.GetActiveAccountsAsync(ct);
        if (knownAccounts.All(a => a.Id != command.ConnectorAccountId))
            throw PoyraException.NotFound("recon.account_not_found", "Bağlantı hesabı bulunamadı.");

        var statement = new ReconStatement
        {
            TenantId = tenant.TenantId,
            ConnectorAccountId = command.ConnectorAccountId,
            StatementDate = command.StatementDate,
            LineCount = command.Lines.Count,
        };
        db.ReconStatements.Add(statement);

        var lineNo = 0;
        foreach (var input in command.Lines)
        {
            lineNo++;
            db.ReconStatementLines.Add(new ReconStatementLine
            {
                TenantId = tenant.TenantId,
                StatementId = statement.Id,
                LineNo = lineNo,
                OrderId = input.OrderId,
                LineType = input.LineType == "refund" ? StatementLineType.Refund : StatementLineType.Sale,
                GrossMinor = input.GrossMinor,
                CommissionMinor = input.CommissionMinor,
                NetMinor = input.NetMinor,
                ValueDate = input.ValueDate,
            });
        }

        var syncLimit = configuration.GetValue<int?>("Recon:SyncMatchLimit") ?? 500;
        if (command.Lines.Count <= syncLimit)
        {
            await db.SaveChangesAsync(ct);
            await matcher.MatchAsync(statement.Id, ct);
            return await StatementSummaries.BuildAsync(db, statement, ct);
        }

        statement.Status = StatementStatus.Matching;
        await db.SaveChangesAsync(ct);

        var tenantId = tenant.TenantId;
        var statementId = statement.Id;
        jobs.Enqueue<StatementMatchJob>(j => j.MatchAsync(tenantId, statementId));

        return await StatementSummaries.BuildAsync(db, statement, ct); // status: matching, sayaçlar 0
    }
}

public sealed class ImportStatementEndpoint(IDispatcher dispatcher)
    : Endpoint<ImportStatementRequest, StatementSummaryDto>
{
    public override void Configure()
    {
        Post("/v1/recon/statements");
        Description(x => x.WithTags("Recon"));
        Summary(s => s.Summary = "Gün sonu ekstresini içe aktarır; küçükte hemen, büyükte Hangfire'da eşleştirir.");
    }

    public override async Task HandleAsync(ImportStatementRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new ImportStatementCommand(
            req.ConnectorAccountId, req.StatementDate, req.Lines), ct), ct);
}

public sealed class UploadStatementRequest
{
    public IFormFile File { get; set; } = null!;
    public Guid ConnectorAccountId { get; set; }
    public DateOnly StatementDate { get; set; }
    public string? Format { get; set; } // varsayılan: poyra_csv
}

public sealed class UploadStatementEndpoint(IDispatcher dispatcher, IEnumerable<IStatementParser> parsers)
    : Endpoint<UploadStatementRequest, StatementSummaryDto>
{
    public override void Configure()
    {
        Post("/v1/recon/statements/upload");
        AllowFileUploads();
        Description(x => x.WithTags("Recon"));
        Summary(s => s.Summary = "Ekstre dosyası yükler (multipart). format=poyra_csv: order_id;type;gross;commission;net;value_date");
    }

    public override async Task HandleAsync(UploadStatementRequest req, CancellationToken ct)
    {
        var formatKey = string.IsNullOrWhiteSpace(req.Format) ? PoyraCsvStatementParser.FormatKey : req.Format;
        var parser = parsers.FirstOrDefault(p => p.Format == formatKey)
            ?? throw new PoyraException(400, "recon.unknown_format",
                $"Bilinmeyen ekstre formatı '{formatKey}'. Mevcut: {string.Join(", ", parsers.Select(p => p.Format))}");

        if (req.File is null || req.File.Length == 0)
            throw new PoyraException(400, "recon.empty_file", "Ekstre dosyası boş.");

        StatementParseResult parsed;
        using (var reader = new StreamReader(req.File.OpenReadStream()))
        {
            parsed = parser.Parse(reader);
        }

        if (parsed.Errors.Count > 0)
            throw new PoyraException(400, "recon.parse_error",
                $"Ekstre ayrıştırılamadı ({parsed.Errors.Count} hata): "
                + string.Join(" | ", parsed.Errors.Take(10)));

        var lines = parsed.Lines
            .Select(l => new StatementLineInput(
                l.OrderId, l.GrossMinor, l.CommissionMinor, l.NetMinor, l.ValueDate, l.LineType))
            .ToList();

        var summary = await dispatcher.Send(new ImportStatementCommand(
            req.ConnectorAccountId, req.StatementDate, lines), ct);
        await Send.OkAsync(summary, ct);
    }
}
