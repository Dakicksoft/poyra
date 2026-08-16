using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Field.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Field.Features;

public sealed record FieldCollectionDto(
    string Id,
    Guid AgentId,
    string AgentCode,
    string? CustomerRef,
    long AmountMinor,
    string Currency,
    string Method,
    string Status,
    string? CheckoutUrl,
    string? PaymentId,
    DateTimeOffset CapturedAtDevice,
    DateTimeOffset OccurredAtServer,

    long DeviceSkewSeconds,
    double? Latitude,
    double? Longitude,
    string? Note);


public sealed record ListFieldCollectionsQuery(
    Guid? AgentId, string? Status, DateOnly? Day) : IQuery<IReadOnlyList<FieldCollectionDto>>;

public sealed class ListFieldCollectionsHandler(FieldDbContext db)
    : IQueryHandler<ListFieldCollectionsQuery, IReadOnlyList<FieldCollectionDto>>
{
    public async Task<IReadOnlyList<FieldCollectionDto>> Handle(
        ListFieldCollectionsQuery query, CancellationToken ct)
    {
        var rows = db.FieldCollections.AsNoTracking()
            .Join(db.FieldAgents.AsNoTracking(), c => c.AgentId, a => a.Id, (c, a) => new { c, a.Code });

        if (query.AgentId is { } agentId)
            rows = rows.Where(x => x.c.AgentId == agentId);

        if (!string.IsNullOrWhiteSpace(query.Status)
            && FieldCollectionStatusMap.FromDb.TryGetValue(query.Status, out var status))
            rows = rows.Where(x => x.c.Status == status);

        if (query.Day is { } day)
        {
            var from = TurkeyTime.StartOfDay(day);
            var to = TurkeyTime.EndOfDay(day);
            rows = rows.Where(x => x.c.OccurredAtServer >= from && x.c.OccurredAtServer < to);
        }

        var list = await rows
            .OrderByDescending(x => x.c.OccurredAtServer)
            .Take(500)
            .Select(x => new
            {
                x.c.PublicId, x.c.AgentId, x.Code, x.c.CustomerRef, x.c.AmountMinor,
                x.c.Currency, x.c.Method, x.c.Status, x.c.CheckoutUrl, x.c.PaymentId,
                x.c.CapturedAtDevice, x.c.OccurredAtServer, x.c.DeviceSkewSeconds,
                x.c.Latitude, x.c.Longitude, x.c.Note,
            })
            .ToListAsync(ct);

        return list.Select(x => new FieldCollectionDto(
            x.PublicId, x.AgentId, x.Code, x.CustomerRef, x.AmountMinor, x.Currency,
            FieldCollectionMethodMap.ToDb[x.Method], FieldCollectionStatusMap.ToDb[x.Status],
            x.CheckoutUrl, x.PaymentId, x.CapturedAtDevice, x.OccurredAtServer,
            x.DeviceSkewSeconds, x.Latitude, x.Longitude, x.Note)).ToList();
    }
}

public sealed class ListFieldCollectionsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<FieldCollectionDto>>
{
    public override void Configure()
    {
        Get("/v1/field/collections");
        Description(x => x.WithTags("Field"));
        Summary(s =>
        {
            s.Summary = "Saha tahsilat kayıtları (en yeni 500).";
            s.Description =
                "`day` süzgeci TR gününe göre ve SUNUCU zamanı (`occurredAtServer`) üzerinden "
                + "çalışır — cihaz beyanına göre süzmek, saati bozuk bir telefonun kaydını yanlış "
                + "güne taşırdı. `deviceSkewSeconds` cihaz saatiyle sunucu arasındaki farktır: "
                + "pozitif değer çevrimdışı geçen süreyi, büyük negatif değer ileri alınmış "
                + "cihaz saatini gösterir.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListFieldCollectionsQuery(
            Query<Guid?>("agentId", isRequired: false),
            Query<string?>("status", isRequired: false),
            Query<DateOnly?>("day", isRequired: false)), ct), ct);
}

public sealed record FieldSettlementRow(
    Guid AgentId,
    string AgentCode,
    string? Region,
    int Count,
    long DeclaredCashMinor,
    long SettledMinor,
    long PendingMinor,
    long FailedMinor,
    int SkewedCount);

public sealed record FieldSettlementReport(
    DateOnly Day,
    long TotalDeclaredCashMinor,
    long TotalSettledMinor,
    IReadOnlyList<FieldSettlementRow> Agents);

public sealed record FieldSettlementQuery(DateOnly Day) : IQuery<FieldSettlementReport>;

public sealed class FieldSettlementHandler(FieldDbContext db)
    : IQueryHandler<FieldSettlementQuery, FieldSettlementReport>
{
    public async Task<FieldSettlementReport> Handle(FieldSettlementQuery query, CancellationToken ct)
    {
        var from = TurkeyTime.StartOfDay(query.Day);
        var to = TurkeyTime.EndOfDay(query.Day);
        var skewLimit = (long)SyncFieldBatchHandler.ImplausibleSkew.TotalSeconds;

        var rows = await db.FieldCollections.AsNoTracking()
            .Where(c => c.OccurredAtServer >= from && c.OccurredAtServer < to)
            .Join(db.FieldAgents.AsNoTracking(), c => c.AgentId, a => a.Id,
                (c, a) => new { c.AgentId, a.Code, a.Region, c.Status, c.AmountMinor, c.DeviceSkewSeconds })
            .ToListAsync(ct);

        var agents = rows
            .GroupBy(x => new { x.AgentId, x.Code, x.Region })
            .Select(g => new FieldSettlementRow(
                g.Key.AgentId, g.Key.Code, g.Key.Region, g.Count(),
                // Nakit AYRI toplanır: kartla tahsil edilenle toplanırsa, hiç görmediğimiz
                // para gerçekten tahsil edilmiş gibi görünür
                g.Where(x => x.Status == FieldCollectionStatus.CashDeclared).Sum(x => x.AmountMinor),
                g.Where(x => x.Status == FieldCollectionStatus.Succeeded).Sum(x => x.AmountMinor),
                g.Where(x => x.Status is FieldCollectionStatus.PendingRequest
                                      or FieldCollectionStatus.LinkIssued).Sum(x => x.AmountMinor),
                g.Where(x => x.Status == FieldCollectionStatus.Failed).Sum(x => x.AmountMinor),
                g.Count(x => Math.Abs(x.DeviceSkewSeconds) > skewLimit)))
            .OrderBy(r => r.AgentCode)
            .ToList();

        return new FieldSettlementReport(
            query.Day,
            agents.Sum(a => a.DeclaredCashMinor),
            agents.Sum(a => a.SettledMinor),
            agents);
    }
}

public sealed class FieldSettlementEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<FieldSettlementReport>
{
    public override void Configure()
    {
        Get("/v1/field/settlement-report");
        Description(x => x.WithTags("Field"));
        Summary(s =>
        {
            s.Summary = "Gün sonu saha özeti — temsilci bazında beyan / kesinleşen ayrımıyla.";
            s.Description =
                "Nakit beyanı ile kartla tahsil edilen AYRI toplanır. Tek toplamda birleştirmek, "
                + "hiç görmediğimiz parayı tahsil edilmiş göstermek olurdu: nakit temsilcinin "
                + "kasaya teslim etmesi gereken meblağdır, Poyra'dan geçmez.\n\n"
                + "Gün TR gününe göredir (UTC+3) ve sunucu zamanı üzerinden hesaplanır. "
                + "`skewedCount`, cihaz saati makul pencerenin dışında kalan kayıt sayısıdır.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new FieldSettlementQuery(
            Query<DateOnly?>("day", isRequired: false)
            ?? TurkeyTime.DayOf(DateTimeOffset.UtcNow)), ct), ct);
}
