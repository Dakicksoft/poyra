using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Ledger.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Ledger.Features;


public sealed record LedgerSettingsDto(int? AnnualFinancingRateBps);

public sealed record SaveLedgerSettingsRequest(int? AnnualFinancingRateBps);

public sealed record SaveLedgerSettingsCommand(int? AnnualFinancingRateBps)
    : Poyra.SharedKernel.Cqrs.ICommand<LedgerSettingsDto>;

public sealed class SaveLedgerSettingsValidator : AbstractValidator<SaveLedgerSettingsCommand>
{
    public SaveLedgerSettingsValidator()
        // %0–%200: üst sınır saçma girişi yakalamak için (350 yazıp %3,5 sanmak gibi),
        // alt sınır sıfır çünkü "oran tanımlı değil" ile "oran sıfır" farklı şeyler
        => RuleFor(x => x.AnnualFinancingRateBps).InclusiveBetween(0, 20_000)
            .When(x => x.AnnualFinancingRateBps.HasValue);
}

public sealed class SaveLedgerSettingsHandler(LedgerDbContext db, TenantContext tenant, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SaveLedgerSettingsCommand, LedgerSettingsDto>
{
    public async Task<LedgerSettingsDto> Handle(SaveLedgerSettingsCommand command, CancellationToken ct)
    {
        var settings = await db.LedgerSettings.SingleOrDefaultAsync(ct);

        if (settings is null)
        {
            settings = new LedgerSettings { TenantId = tenant.TenantId };
            db.LedgerSettings.Add(settings);
        }

        settings.AnnualFinancingRateBps = command.AnnualFinancingRateBps;
        settings.UpdatedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);
        return new LedgerSettingsDto(settings.AnnualFinancingRateBps);
    }
}

public sealed class SaveLedgerSettingsEndpoint(IDispatcher dispatcher)
    : Endpoint<SaveLedgerSettingsRequest, LedgerSettingsDto>
{
    public override void Configure()
    {
        Post("/v1/ledger/settings");
        Description(x => x.WithTags("Ledger"));
        Summary(s => s.Summary =
            "Defter ayarı: işyerinin yıllık finansman maliyeti (baz puan, 3500 = %35). "
            + "Valör gecikmesinin parasal karşılığı bununla hesaplanır.");
    }

    public override async Task HandleAsync(SaveLedgerSettingsRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new SaveLedgerSettingsCommand(req.AnnualFinancingRateBps), ct), ct);
}


public sealed record ValueDateCostRow(
    Guid ConnectorAccountId,
    int LateCount,
    long LateAmountMinor,
    int TotalDelayDays,
    int MaxDelayDays,
    long CostMinor);

public sealed record ValueDateCostReport(
    int? RateBps,
    long TotalCostMinor,
    int LateCount,
    long LateAmountMinor,
    double AverageDelayDays,
    int EarlyCount,
    IReadOnlyList<ValueDateCostRow> Accounts);

public sealed record ValueDateCostQuery : IQuery<ValueDateCostReport>;

public sealed class ValueDateCostHandler(LedgerDbContext db, IClock clock)
    : IQueryHandler<ValueDateCostQuery, ValueDateCostReport>
{
    public async Task<ValueDateCostReport> Handle(ValueDateCostQuery query, CancellationToken ct)
    {
        var settings = await db.LedgerSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        var rate = settings?.AnnualFinancingRateBps;

        var today = TurkeyTime.DayOf(clock.UtcNow);

        // Yalnız valörü BİLİNEN alacaklar: anlaşması olmayan kayıtta gecikme
        // hesaplanamaz ve tahmin edilmez
        var rows = await db.BankReceivables.AsNoTracking()
            .Where(r => r.ExpectedValueDate != null && r.Status != ReceivableStatus.Reversed)
            .ToListAsync(ct);

        var late = new List<(BankReceivable Row, int Delay, long Cost)>();
        var early = 0;

        foreach (var row in rows)
        {
            var delay = row.ValueDateDelayDays(today) ?? 0;
            if (delay < 0)
            {
                early++;
                continue;
            }

            if (delay == 0)
                continue;

            // Maliyet NET tutar üzerinden: işyerinin eline geçmeyen para odur,
            // brüt değil (komisyon zaten kesilerek gelir)
            var amount = row.ConfirmedNetMinor ?? row.ExpectedNetMinor ?? 0;
            late.Add((row, delay, rate is { } bps ? ValueDateCost.Of(amount, delay, bps) : 0));
        }

        var accounts = late
            .GroupBy(x => x.Row.ConnectorAccountId)
            .Select(g => new ValueDateCostRow(
                g.Key,
                g.Count(),
                g.Sum(x => x.Row.ConfirmedNetMinor ?? x.Row.ExpectedNetMinor ?? 0),
                g.Sum(x => x.Delay),
                g.Max(x => x.Delay),
                g.Sum(x => x.Cost)))
            .OrderByDescending(a => a.CostMinor)
            .ToList();

        return new ValueDateCostReport(
            rate,
            late.Sum(x => x.Cost),
            late.Count,
            late.Sum(x => x.Row.ConfirmedNetMinor ?? x.Row.ExpectedNetMinor ?? 0),
            late.Count == 0 ? 0 : Math.Round(late.Average(x => (double)x.Delay), 1),
            early,
            accounts);
    }
}

public sealed class ValueDateCostEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<ValueDateCostReport>
{
    public override void Configure()
    {
        Get("/v1/ledger/value-date-cost");
        Description(x => x.WithTags("Ledger"));
        Summary(s =>
        {
            s.Summary = "Valör gecikmesinin parasal karşılığı — geç ödemenin finansman maliyeti.";
            s.Description =
                "\"Banka 3 gün geç ödedi\" bir şikâyettir; \"3 gün geç ödediği için bu ay "
                + "18.400 ₺ finansman maliyetine girdiniz\" bir karardır.\n\n"
                + "`tutar × (yıllık oran / 365) × gecikme günü`. Oran işyerinin **kendi** "
                + "finansman maliyetidir ve `POST /v1/ledger/settings` ile tanımlanır — "
                + "tanımlı değilse maliyet **hesaplanmaz** (uydurulmuş bir oran, olmayan bir "
                + "zararı varmış gibi gösterir).\n\n"
                + "Erken ödemeler maliyetle **netleştirilmez**: ara sıra erken ödeyen bir banka "
                + "kronik gecikmesini gizleyebilirdi. Ayrı sayılır.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ValueDateCostQuery(), ct), ct);
}
