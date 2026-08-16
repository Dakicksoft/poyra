using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Ledger.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Ledger.Features;

public sealed record ReceivableDto(
    string Id,
    Guid ConnectorAccountId,
    string Kind,
    string Status,
    string AttemptId,
    string? PaymentId,
    long GrossMinor,
    string Currency,
    int Installments,
    int? ExpectedRateBps,
    long? ExpectedCommissionMinor,
    long? ExpectedNetMinor,
    DateOnly? ExpectedValueDate,
    long? ConfirmedCommissionMinor,
    long? ConfirmedNetMinor,
    DateOnly? ConfirmedValueDate,
    long? CommissionDeltaMinor,
    int? ValueDateDelayDays,

    DateTimeOffset CapturedAtServer);

public sealed record ReceivableSummary(
    DateOnly Today,
    long TotalOutstandingMinor,
    long DueTodayMinor,
    long OverdueMinor,
    int OverdueCount,
    long ConfirmedMinor,
    int UnknownTermsCount,
    long CommissionDeltaMinor,
    IReadOnlyList<ReceivableAccountRow> Accounts);

public sealed record ReceivableAccountRow(
    Guid ConnectorAccountId,
    int Count,
    long OutstandingMinor,
    long OverdueMinor,
    long CommissionDeltaMinor,
    int MaxDelayDays);

public sealed record ReceivableSummaryQuery : IQuery<ReceivableSummary>;

public sealed class ReceivableSummaryHandler(LedgerDbContext db, IClock clock)
    : IQueryHandler<ReceivableSummaryQuery, ReceivableSummary>
{
    public async Task<ReceivableSummary> Handle(ReceivableSummaryQuery query, CancellationToken ct)
    {
        var today = TurkeyTime.DayOf(clock.UtcNow);

        // Kapanmış alacak (Settled/Reversed) açık bakiyeye girmez
        var open = await db.BankReceivables.AsNoTracking()
            .Where(r => r.Status != ReceivableStatus.Settled && r.Status != ReceivableStatus.Reversed)
            .ToListAsync(ct);

        // Beklenen net bilinmiyorsa BRÜT kullanılır: anlaşması olmayan bir tahsilatı
        // toplamdan düşmek, işyerine gerçekte olandan az alacak göstermek olurdu.
        long Outstanding(BankReceivable r) => r.ExpectedNetMinor ?? r.GrossMinor;

        var accounts = open
            .GroupBy(r => r.ConnectorAccountId)
            .Select(g => new ReceivableAccountRow(
                g.Key,
                g.Count(),
                g.Sum(Outstanding),
                g.Where(r => r.IsOverdue(today)).Sum(Outstanding),
                g.Sum(r => r.CommissionDeltaMinor ?? 0),
                g.Select(r => r.ValueDateDelayDays(today) ?? 0).DefaultIfEmpty(0).Max()))
            .OrderByDescending(a => a.OutstandingMinor)
            .ToList();

        return new ReceivableSummary(
            today,
            open.Sum(Outstanding),
            open.Where(r => r.ExpectedValueDate == today).Sum(Outstanding),
            open.Where(r => r.IsOverdue(today)).Sum(Outstanding),
            open.Count(r => r.IsOverdue(today)),
            open.Where(r => r.Status == ReceivableStatus.BankConfirmed).Sum(Outstanding),
            open.Count(r => r.ExpectedRateBps is null),
            open.Sum(r => r.CommissionDeltaMinor ?? 0),
            accounts);
    }
}

public sealed class ReceivableSummaryEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<ReceivableSummary>
{
    public override void Configure()
    {
        Get("/v1/receivables/summary");
        Description(x => x.WithTags("Ledger"));
        Summary(s =>
        {
            s.Summary = "Bankalardan alacak özeti — toplam, bugün gelmesi gereken, geciken.";
            s.Description =
                "**Bu Poyra'nın bakiyesi DEĞİLDİR.** Poyra lisanslı ödeme kuruluşu değildir; "
                + "parayı ne alır, ne tutar, ne dağıtır. Burada tutulan kayıt bankanın işyerine "
                + "olan borcunun aynasıdır — muhasebe kaydı, fon değil.\n\n"
                + "`expected*` alanları ANLAŞMADAN hesaplanır (bizim iddiamız), `confirmed*` "
                + "alanları POS EKSTRESİNDEN gelir (bankanın söylediği). İkisi bilinçli olarak "
                + "ayrı durur: birleştirmek aradaki farkı — yani asıl değeri — yok ederdi.\n\n"
                + "`overdue` = valörü geçmiş ve banka HÂLÂ TEYİT ETMEMİŞ. Bankanın kabul ettiği "
                + "ama henüz ödemediği para ayrı bir sorundur ve hesap ekstresi mutabakatı gerektirir.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ReceivableSummaryQuery(), ct), ct);
}


public sealed record ListReceivablesQuery(string? Status, Guid? ConnectorAccountId, DateOnly? ValueDate)
    : IQuery<IReadOnlyList<ReceivableDto>>;

public sealed class ListReceivablesHandler(LedgerDbContext db, IClock clock)
    : IQueryHandler<ListReceivablesQuery, IReadOnlyList<ReceivableDto>>
{
    public async Task<IReadOnlyList<ReceivableDto>> Handle(
        ListReceivablesQuery query, CancellationToken ct)
    {
        var today = TurkeyTime.DayOf(clock.UtcNow);
        var rows = db.BankReceivables.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Status)
            && ReceivableStatusMap.FromDb.TryGetValue(query.Status, out var status))
            rows = rows.Where(r => r.Status == status);

        if (query.ConnectorAccountId is { } accountId)
            rows = rows.Where(r => r.ConnectorAccountId == accountId);

        if (query.ValueDate is { } valueDate)
            rows = rows.Where(r => r.ExpectedValueDate == valueDate);

        var list = await rows
            .OrderByDescending(r => r.CapturedAtServer)
            .Take(500)
            .ToListAsync(ct);

        return list.Select(r => new ReceivableDto(
            r.PublicId, r.ConnectorAccountId,
            ReceivableKindMap.ToDb[r.Kind], ReceivableStatusMap.ToDb[r.Status],
            r.AttemptPublicId, r.PaymentPublicId, r.GrossMinor, r.Currency, r.Installments,
            r.ExpectedRateBps, r.ExpectedCommissionMinor, r.ExpectedNetMinor, r.ExpectedValueDate,
            r.ConfirmedCommissionMinor, r.ConfirmedNetMinor, r.ConfirmedValueDate,
            r.CommissionDeltaMinor, r.ValueDateDelayDays(today), r.CapturedAtServer)).ToList();
    }
}

public sealed class ListReceivablesEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<ReceivableDto>>
{
    public override void Configure()
    {
        Get("/v1/receivables");
        Description(x => x.WithTags("Ledger"));
        Summary(s => s.Summary =
            "Bankalardan alacak kayıtları (en yeni 500). Süzgeçler: status, connectorAccountId, valueDate.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListReceivablesQuery(
            Query<string?>("status", isRequired: false),
            Query<Guid?>("connectorAccountId", isRequired: false),
            Query<DateOnly?>("valueDate", isRequired: false)), ct), ct);
}
