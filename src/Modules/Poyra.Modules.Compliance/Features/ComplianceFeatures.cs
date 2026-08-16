using System.Globalization;
using System.Text;
using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Compliance.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Compliance.Features;

public sealed record AuditEntryDto(
    Guid Id, string Actor, string Action, string ResourceType, string? ResourceId,
    string Summary, string Metadata, string? IpAddress, DateTimeOffset CreatedAt);

/// <param name="From">TR günü başlangıcı (dahil).</param>
/// <param name="To">TR günü bitişi (dahil).</param>
public sealed record ListAuditQuery(
    string? Actor, string? Action, string? ResourceType, string? ResourceId,
    DateOnly? From, DateOnly? To, int Limit) : IQuery<IReadOnlyList<AuditEntryDto>>;

public sealed class ListAuditHandler(ComplianceDbContext db)
    : IQueryHandler<ListAuditQuery, IReadOnlyList<AuditEntryDto>>
{
    /// <summary>Türkiye sabit UTC+3'tür — denetçi TR gününe göre arar.</summary>
    private static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);

    public async Task<IReadOnlyList<AuditEntryDto>> Handle(ListAuditQuery query, CancellationToken ct)
        => await Filter(db.AuditLog.AsNoTracking(), query)
            .OrderByDescending(e => e.CreatedAt)
            .Take(Math.Clamp(query.Limit, 1, 1000))
            .Select(e => new AuditEntryDto(
                e.Id, e.Actor, e.Action, e.ResourceType, e.ResourceId,
                e.Summary, e.Metadata, e.IpAddress, e.CreatedAt))
            .ToListAsync(ct);

    internal static IQueryable<AuditLogEntry> Filter(IQueryable<AuditLogEntry> source, ListAuditQuery query)
    {
        if (query.Actor is { Length: > 0 } actor)
            source = source.Where(e => e.Actor == actor);
        if (query.Action is { Length: > 0 } action)
            source = source.Where(e => e.Action.StartsWith(action));
        if (query.ResourceType is { Length: > 0 } resourceType)
            source = source.Where(e => e.ResourceType == resourceType);
        if (query.ResourceId is { Length: > 0 } resourceId)
            source = source.Where(e => e.ResourceId == resourceId);

        // Npgsql timestamptz'e yalnız UTC ofseti yazar — TR gün sınırı çevrilir
        if (query.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TurkeyOffset).ToUniversalTime();
            source = source.Where(e => e.CreatedAt >= start);
        }

        if (query.To is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TurkeyOffset)
                .ToUniversalTime();
            source = source.Where(e => e.CreatedAt < end);
        }

        return source;
    }
}

public sealed class ListAuditEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<IReadOnlyList<AuditEntryDto>>
{
    public override void Configure()
    {
        Get("/v1/compliance/audit");
        Description(x => x.WithTags("Compliance"));
        Summary(s => s.Summary =
            "Birleşik denetim defteri: kim, ne zaman, hangi kayda dokundu. "
            + "Filtreler: actor, action, resourceType, resourceId, from, to (TR günü), limit.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListAuditQuery(
            Query<string?>("actor", isRequired: false),
            Query<string?>("action", isRequired: false),
            Query<string?>("resourceType", isRequired: false),
            Query<string?>("resourceId", isRequired: false),
            Query<DateOnly?>("from", isRequired: false),
            Query<DateOnly?>("to", isRequired: false),
            Query<int?>("limit", isRequired: false) ?? 200), ct), ct);
}

public sealed record ExportAuditQuery(ListAuditQuery Filter) : IQuery<string>;

/// <summary>
/// Denetim defterini CSV olarak verir — dış denetçi ve kurum talebi için.
/// Sınır 50.000 satır: daha büyüğü tarih aralığı daraltılarak alınmalıdır,
/// yoksa tek istek belleği doldurur.
/// </summary>
public sealed class ExportAuditHandler(ComplianceDbContext db)
    : IQueryHandler<ExportAuditQuery, string>
{
    public const int MaxRows = 50_000;

    public async Task<string> Handle(ExportAuditQuery query, CancellationToken ct)
    {
        var rows = await ListAuditHandler.Filter(db.AuditLog.AsNoTracking(), query.Filter)
            .OrderBy(e => e.CreatedAt)
            .Take(MaxRows)
            .ToListAsync(ct);

        var csv = new StringBuilder();
        csv.AppendLine("zaman_tr;aktor;eylem;kaynak_turu;kaynak_id;ozet;ip");

        foreach (var row in rows)
        {
            // TR saatiyle yazılır: denetçi UTC'ye çevirmek zorunda kalmamalı
            var moment = row.CreatedAt.ToOffset(TimeSpan.FromHours(3))
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            csv.Append(moment).Append(';')
                .Append(Escape(row.Actor)).Append(';')
                .Append(Escape(row.Action)).Append(';')
                .Append(Escape(row.ResourceType)).Append(';')
                .Append(Escape(row.ResourceId)).Append(';')
                .Append(Escape(row.Summary)).Append(';')
                .Append(Escape(row.IpAddress)).AppendLine();
        }

        return csv.ToString();
    }

    /// <summary>Ayraç noktalı virgül: Excel TR yerelinde virgül ondalık ayracıdır.</summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value.Contains(';') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
    }
}

public sealed class ExportAuditEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/v1/compliance/audit/export");
        Description(x => x.WithTags("Compliance"));
        Summary(s => s.Summary = "Denetim defterini CSV olarak indirir (en çok 50.000 satır).");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var csv = await dispatcher.Ask(new ExportAuditQuery(new ListAuditQuery(
            Query<string?>("actor", isRequired: false),
            Query<string?>("action", isRequired: false),
            Query<string?>("resourceType", isRequired: false),
            Query<string?>("resourceId", isRequired: false),
            Query<DateOnly?>("from", isRequired: false),
            Query<DateOnly?>("to", isRequired: false),
            ExportAuditHandler.MaxRows)), ct);

        // UTF-8 BOM: Excel BOM'suz dosyada Türkçe karakterleri bozar
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        await Send.BytesAsync(bytes, "denetim-defteri.csv", "text/csv; charset=utf-8", cancellation: ct);
    }
}

public sealed record SuspiciousReportDto(
    string Id, string PaymentIds, string? CustomerRef, string Rationale,
    string Status, string? Resolution, DateTimeOffset? ResolvedAt, DateTimeOffset CreatedAt);

public sealed record OpenSuspiciousReportRequest(
    IReadOnlyList<string> PaymentIds, string Rationale, string? CustomerRef = null);

public sealed record OpenSuspiciousReportCommand(
    IReadOnlyList<string> PaymentIds, string Rationale, string? CustomerRef)
    : Poyra.SharedKernel.Cqrs.ICommand<SuspiciousReportDto>;

public sealed class OpenSuspiciousReportValidator : AbstractValidator<OpenSuspiciousReportCommand>
{
    public OpenSuspiciousReportValidator()
    {
        RuleFor(x => x.PaymentIds).NotEmpty().WithMessage("En az bir ödeme kimliği gerekli.");
        RuleFor(x => x.Rationale).NotEmpty().MaximumLength(4000)
            .WithMessage("Şüphe gerekçesi zorunlu — gerekçesiz kayıt denetimde savunulamaz.");
        RuleFor(x => x.CustomerRef).MaximumLength(100);
    }
}

/// <summary>
/// Şüpheli işlem kaydı açar.
///
/// Bu bir MASAK bildirimi DEĞİLDİR; iç incelemenin defteridir. Fiili bildirim
/// lisanslı kuruluşun MASAK erişimiyle yapılır (bkz. SuspiciousActivityReport).
/// </summary>
public sealed class OpenSuspiciousReportHandler(
    ComplianceDbContext db, TenantContext tenant, UserContext user)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<OpenSuspiciousReportCommand, SuspiciousReportDto>
{
    public async Task<SuspiciousReportDto> Handle(OpenSuspiciousReportCommand command, CancellationToken ct)
    {
        var report = new SuspiciousActivityReport
        {
            TenantId = tenant.TenantId,
            PaymentIds = string.Join(",", command.PaymentIds.Select(p => p.Trim())),
            CustomerRef = command.CustomerRef,
            Rationale = command.Rationale.Trim(),
            OpenedByUserId = user.UserId,
        };

        db.SuspiciousReports.Add(report);
        await db.SaveChangesAsync(ct);

        return Map(report);
    }

    internal static SuspiciousReportDto Map(SuspiciousActivityReport r)
        => new(r.PublicId, r.PaymentIds, r.CustomerRef, r.Rationale,
            SuspiciousReportStatusMap.ToDb[r.Status], r.Resolution, r.ResolvedAt, r.CreatedAt);
}

public sealed class OpenSuspiciousReportEndpoint(IDispatcher dispatcher)
    : Endpoint<OpenSuspiciousReportRequest, SuspiciousReportDto>
{
    public override void Configure()
    {
        Post("/v1/compliance/reports");
        Description(x => x.WithTags("Compliance"));
        Summary(s => s.Summary =
            "Şüpheli işlem kaydı açar (iç inceleme defteri). MASAK'a fiili bildirim "
            + "lisanslı kuruluşun yükümlülüğüdür ve bu uçtan YAPILMAZ.");
    }

    public override async Task HandleAsync(OpenSuspiciousReportRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new OpenSuspiciousReportCommand(
            req.PaymentIds, req.Rationale, req.CustomerRef), ct), ct);
}

public sealed record ListSuspiciousReportsQuery(string? Status) : IQuery<IReadOnlyList<SuspiciousReportDto>>;

public sealed class ListSuspiciousReportsHandler(ComplianceDbContext db)
    : IQueryHandler<ListSuspiciousReportsQuery, IReadOnlyList<SuspiciousReportDto>>
{
    public async Task<IReadOnlyList<SuspiciousReportDto>> Handle(
        ListSuspiciousReportsQuery query, CancellationToken ct)
    {
        var reports = db.SuspiciousReports.AsNoTracking();

        if (query.Status is { Length: > 0 } status
            && SuspiciousReportStatusMap.FromDb.TryGetValue(status, out var parsed))
            reports = reports.Where(r => r.Status == parsed);

        var rows = await reports.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync(ct);
        return rows.Select(OpenSuspiciousReportHandler.Map).ToList();
    }
}

public sealed class ListSuspiciousReportsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<SuspiciousReportDto>>
{
    public override void Configure()
    {
        Get("/v1/compliance/reports");
        Description(x => x.WithTags("Compliance"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListSuspiciousReportsQuery(
            Query<string?>("status", isRequired: false)), ct), ct);
}

public sealed record ResolveSuspiciousReportRequest(string Status, string Resolution);

public sealed record ResolveSuspiciousReportCommand(string ReportId, string Status, string Resolution)
    : Poyra.SharedKernel.Cqrs.ICommand<SuspiciousReportDto>;

public sealed class ResolveSuspiciousReportHandler(
    ComplianceDbContext db, UserContext user, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ResolveSuspiciousReportCommand, SuspiciousReportDto>
{
    public async Task<SuspiciousReportDto> Handle(
        ResolveSuspiciousReportCommand command, CancellationToken ct)
    {
        var report = await db.SuspiciousReports.SingleOrDefaultAsync(r => r.PublicId == command.ReportId, ct)
            ?? throw PoyraException.NotFound("compliance.report_not_found", "Kayıt bulunamadı.");

        if (!SuspiciousReportStatusMap.FromDb.TryGetValue(command.Status, out var status))
            throw new PoyraException(400, "compliance.invalid_status",
                $"Geçersiz durum. Geçerli: {string.Join(", ", SuspiciousReportStatusMap.FromDb.Keys)}");

        report.Resolve(status, command.Resolution, user.UserId, clock.UtcNow);
        await db.SaveChangesAsync(ct);

        return OpenSuspiciousReportHandler.Map(report);
    }
}

public sealed class ResolveSuspiciousReportEndpoint(IDispatcher dispatcher)
    : Endpoint<ResolveSuspiciousReportRequest, SuspiciousReportDto>
{
    public override void Configure()
    {
        Post("/v1/compliance/reports/{id}/resolve");
        Description(x => x.WithTags("Compliance"));
        Summary(s => s.Summary =
            "İncelemeyi sonuçlandırır: dismissed | ready_to_file | filed. Gerekçe zorunlu.");
    }

    public override async Task HandleAsync(ResolveSuspiciousReportRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new ResolveSuspiciousReportCommand(
            Route<string>("id")!, req.Status, req.Resolution), ct), ct);
}
