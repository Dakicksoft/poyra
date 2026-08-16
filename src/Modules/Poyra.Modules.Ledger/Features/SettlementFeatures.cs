using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Ledger.Domain;
using Poyra.Modules.Ledger.Infrastructure;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Ledger.Features;

public sealed record SettlementStatementDto(
    string Id, Guid ConnectorAccountId, string Format, string FileName,
    int LineCount, DateOnly? FromDate, DateOnly? ToDate, DateTimeOffset CreatedAt);

public sealed record SettlementFindingDto(
    Guid ConnectorAccountId,
    DateOnly ValueDate,
    string Outcome,
    long ExpectedMinor,
    long ActualMinor,
    long DeltaMinor,
    int ReceivableCount,
    DateTimeOffset DetectedAt);
// ------------------------------------------------------------------ yükleme

public sealed record ImportSettlementCommand(
    Guid ConnectorAccountId, string Format, string FileName, string Content)
    : Poyra.SharedKernel.Cqrs.ICommand<SettlementStatementDto>;

public sealed class ImportSettlementHandler(
    LedgerDbContext db, TenantContext tenant, SettlementMatcher matcher,
    IEnumerable<ISettlementParser> parsers)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ImportSettlementCommand, SettlementStatementDto>
{
    public async Task<SettlementStatementDto> Handle(
        ImportSettlementCommand command, CancellationToken ct)
    {
        var parser = parsers.FirstOrDefault(p =>
            string.Equals(p.Format, command.Format, StringComparison.OrdinalIgnoreCase))
            ?? throw PoyraException.Conflict("settlement.unknown_format",
                $"Bilinmeyen ekstre biçimi: {command.Format}. Desteklenen: "
                + string.Join(", ", parsers.Select(p => p.Format)));

        using var reader = new StringReader(command.Content);
        var parsed = parser.Parse(reader);

        // Ayrıştırma hatası SESSİZ GEÇİLMEZ: yarım okunmuş bir ekstre, olmayan bir
        // "eksik yatırma" bulgusu üretir ve bankaya haksız itiraz yazdırır.
        if (parsed.Errors.Count > 0)
            throw new PoyraException(400, "settlement.parse_error",
                $"Ekstre ayrıştırılamadı ({parsed.Errors.Count} hata): "
                + string.Join("; ", parsed.Errors.Take(5)));

        if (parsed.Lines.Count == 0)
            throw new PoyraException(400, "settlement.empty", "Ekstrede hareket bulunamadı.");

        var statement = new SettlementStatement
        {
            TenantId = tenant.TenantId,
            ConnectorAccountId = command.ConnectorAccountId,
            Format = parser.Format,
            FileName = command.FileName,
            LineCount = parsed.Lines.Count,
            FromDate = parsed.Lines.Min(l => l.ValueDate),
            ToDate = parsed.Lines.Max(l => l.ValueDate),
        };

        db.SettlementStatements.Add(statement);
        db.SettlementLines.AddRange(parsed.Lines.Select(l => new SettlementLine
        {
            TenantId = tenant.TenantId,
            StatementId = statement.Id,
            LineNo = l.LineNo,
            ValueDate = l.ValueDate,
            AmountMinor = l.AmountMinor,
            Description = l.Description,
            Reference = l.Reference,
        }));

        await db.SaveChangesAsync(ct);

        // Eşleştirme yükleme ile AYNI istekte koşar: işyeri dosyayı yükleyip
        // sonucu hemen görmeli — "birazdan bakın" bir cevap değildir.
        await matcher.MatchAsync(statement.Id, ct);

        return new SettlementStatementDto(
            statement.PublicId, statement.ConnectorAccountId, statement.Format,
            statement.FileName, statement.LineCount, statement.FromDate, statement.ToDate,
            statement.CreatedAt);
    }
}

public sealed class ImportSettlementEndpoint(IDispatcher dispatcher) : Endpoint<EmptyRequest>
{
    public override void Configure()
    {
        Post("/v1/settlements/upload");
        AllowFileUploads();
        Description(x => x.WithTags("Ledger"));
        Summary(s =>
        {
            s.Summary = "Banka HESAP ekstresi yükler ve üç yönlü mutabakatı koşar.";
            s.Description =
                "Zincirin üçüncü halkası. POS ekstresi bankanın **ödeyeceğim** dediği yerdir; "
                + "hesap ekstresi **ödedim** dediği yer. İkisinin tutmadığı durumlar Türkiye'de "
                + "sık (eksik ödeme, geç valör, netleştirilmiş kesinti) ve bugün kimse kontrol "
                + "etmiyor.\n\n"
                + "Eşleştirme **satır-satır değil GÜN-KÜME** yapılır: banka hasılatı toplu "
                + "yatırır, bir hesap hareketi yüzlerce tahsilatın netidir.\n\n"
                + "Biçimler: `mt940` (SWIFT — TR kurumsal bankacılığın standardı), `poyra_csv`.\n\n"
                + "Tutar karşılaştırması **tam** yapılır, tolerans yoktur: sistematik bir kuruş "
                + "farkı milyonlarca işlemde gerçek paradır.";
        });
    }

    public override async Task HandleAsync(EmptyRequest _, CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var file = form.Files.FirstOrDefault()
            ?? throw new PoyraException(400, "settlement.file_required", "Ekstre dosyası zorunludur.");

        if (!Guid.TryParse(form["connectorAccountId"], out var accountId))
            throw new PoyraException(400, "settlement.account_required",
                "connectorAccountId zorunludur — ekstre hangi POS hesabının hasılatını taşıyor?");

        var format = form["format"].ToString() is { Length: > 0 } f ? f : "poyra_csv";

        using var reader = new StreamReader(file.OpenReadStream());
        var content = await reader.ReadToEndAsync(ct);

        await Send.OkAsync(await dispatcher.Send(
            new ImportSettlementCommand(accountId, format, file.FileName, content), ct), ct);
    }
}

public sealed record SettlementReport(
    long ShortfallMinor,
    long OverpaidMinor,
    int SettledDays,
    int ShortDays,
    int MissingDays,
    IReadOnlyList<SettlementFindingDto> Findings);

public sealed record SettlementReportQuery(string? Outcome) : IQuery<SettlementReport>;

public sealed class SettlementReportHandler(LedgerDbContext db)
    : IQueryHandler<SettlementReportQuery, SettlementReport>
{
    public async Task<SettlementReport> Handle(SettlementReportQuery query, CancellationToken ct)
    {
        var rows = await db.SettlementFindings.AsNoTracking()
            .OrderByDescending(f => f.ValueDate)
            .Take(500)
            .ToListAsync(ct);

        var filtered = !string.IsNullOrWhiteSpace(query.Outcome)
                       && SettlementOutcomeMap.FromDb.TryGetValue(query.Outcome, out var outcome)
            ? rows.Where(f => f.Outcome == outcome).ToList()
            : rows;

        return new SettlementReport(
            // Eksik yatanların toplamı POZİTİF gösterilir: "şu kadar para eksik"
            -rows.Where(f => f.DeltaMinor < 0).Sum(f => f.DeltaMinor),
            rows.Where(f => f.DeltaMinor > 0).Sum(f => f.DeltaMinor),
            rows.Count(f => f.Outcome == SettlementOutcome.Settled),
            rows.Count(f => f.Outcome == SettlementOutcome.Short),
            rows.Count(f => f.Outcome == SettlementOutcome.Missing),
            filtered.Select(f => new SettlementFindingDto(
                f.ConnectorAccountId, f.ValueDate, SettlementOutcomeMap.ToDb[f.Outcome],
                f.ExpectedMinor, f.ActualMinor, f.DeltaMinor, f.ReceivableCount, f.CreatedAt))
                .ToList());
    }
}

public sealed class SettlementReportEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<SettlementReport>
{
    public override void Configure()
    {
        Get("/v1/settlements/report");
        Description(x => x.WithTags("Ledger"));
        Summary(s =>
        {
            s.Summary = "Üç yönlü mutabakat raporu — söz verilen para gerçekten geldi mi.";
            s.Description =
                "`shortfallMinor` **bankadan sorulacak paradır**: POS ekstresinde ödeneceği "
                + "söylenen ama hesaba geçmeyen tutar.\n\n"
                + "`missing` = o gün hiç para yatmamış (en ağır bulgu). "
                + "`short` = yattı ama eksik. `over` = fazla yattı — genellikle başka bir günün "
                + "hasılatı karışmıştır.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(
            new SettlementReportQuery(Query<string?>("outcome", isRequired: false)), ct), ct);
}
