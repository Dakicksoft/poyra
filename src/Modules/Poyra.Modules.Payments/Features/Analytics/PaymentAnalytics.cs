using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Payments.Features.Analytics;


public sealed record BreakdownRow(
    string Key, string Label, int Attempts, int Succeeded, double SuccessRate, long GmvMinor,
    Guid? AccountId = null);

public sealed record FailureRow(string Code, string Label, int Count, double Share);

public sealed record HourRow(int Hour, int Attempts, int Succeeded, long GmvMinor);

public sealed record DayRow(DateOnly Day, int Attempts, int Succeeded, long GmvMinor);

public sealed record AnalyticsOverviewDto(
    int Days,
    DateOnly FromDay,
    DateOnly ToDay,
    int PaymentCount,
    int Attempts,
    int Succeeded,
    double SuccessRate,
    long GmvMinor,
    long RefundedMinor,
    long AverageBasketMinor,
    int? MedianLatencyMs,
    IReadOnlyList<BreakdownRow> ByConnector,
    IReadOnlyList<BreakdownRow> ByInstallment,
    IReadOnlyList<BreakdownRow> ByCardProgram,
    IReadOnlyList<FailureRow> Failures,
    IReadOnlyList<HourRow> ByHour,
    IReadOnlyList<DayRow> ByDay);

public sealed record AnalyticsOverviewQuery(int Days) : IQuery<AnalyticsOverviewDto>;


public sealed class AnalyticsOverviewHandler(PaymentsDbContext db, IClock clock)
    : IQueryHandler<AnalyticsOverviewQuery, AnalyticsOverviewDto>
{
    public const int MinimumSample = 20;

    public async Task<AnalyticsOverviewDto> Handle(AnalyticsOverviewQuery query, CancellationToken ct)
    {
        var days = Math.Clamp(query.Days, 1, 365);
        var toDay = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime.AddHours(3));
        var fromDay = toDay.AddDays(-(days - 1));
        // Npgsql timestamptz'a YALNIZ UTC offset'i yazar: TR gününün başlangıcı UTC'ye çevrilir
        var since = new DateTimeOffset(fromDay.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3))
            .ToUniversalTime();

        var attempts = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.CreatedAt >= since)
            .Select(a => new AttemptRow(
                a.Status, a.ConnectorKey, a.ConnectorAccountId, a.AmountMinor, a.Installments,
                a.CardProgram, a.LatencyMs, a.ErrorUnifiedCode, a.CreatedAt, a.CapturedAt))
            .ToListAsync(ct);

        var paymentCount = await db.PaymentIntents.AsNoTracking()
            .CountAsync(p => p.CreatedAt >= since, ct);

        var refunded = await db.Refunds.AsNoTracking()
            .Where(r => r.CreatedAt >= since && r.Status == RefundStatus.Succeeded)
            .SumAsync(r => (long?)r.AmountMinor, ct) ?? 0;

        var succeeded = attempts.Where(a => a.Status == AttemptStatus.Captured).ToList();
        var gmv = succeeded.Sum(a => a.AmountMinor);

        return new AnalyticsOverviewDto(
            days, fromDay, toDay,
            paymentCount,
            attempts.Count,
            succeeded.Count,
            Rate(succeeded.Count, attempts.Count),
            gmv,
            refunded,
            succeeded.Count == 0 ? 0 : gmv / succeeded.Count,
            Median(succeeded.Select(a => a.LatencyMs).OfType<int>().ToList()),
            GroupByAccount(attempts),
            Group(attempts, a => a.Installments.ToString(),
                key => key == "1" ? "Tek çekim" : $"{key} taksit"),
            Group(attempts, a => a.CardProgram ?? "(bilinmiyor)", key => key),
            Failures(attempts),
            Hours(attempts),
            Days(attempts, fromDay, toDay));
    }

    private sealed record AttemptRow(
        AttemptStatus Status, string ConnectorKey, Guid ConnectorAccountId, long AmountMinor,
        int Installments, string? CardProgram, int? LatencyMs, string? ErrorUnifiedCode,
        DateTimeOffset CreatedAt, DateTimeOffset? CapturedAt);

    private static IReadOnlyList<BreakdownRow> GroupByAccount(List<AttemptRow> attempts)
        => attempts
            .GroupBy(a => (a.ConnectorAccountId, a.ConnectorKey))
            .Select(g =>
            {
                var ok = g.Where(a => a.Status == AttemptStatus.Captured).ToList();
                return new BreakdownRow(
                    g.Key.ConnectorAccountId.ToString(), g.Key.ConnectorKey, g.Count(), ok.Count,
                    Rate(ok.Count, g.Count()), ok.Sum(a => a.AmountMinor), g.Key.ConnectorAccountId);
            })
            .OrderByDescending(r => r.GmvMinor)
            .ThenByDescending(r => r.Attempts)
            .ToList();

    private static IReadOnlyList<BreakdownRow> Group(
        List<AttemptRow> attempts, Func<AttemptRow, string> keySelector, Func<string, string> labeller)
        => attempts
            .GroupBy(keySelector)
            .Select(g =>
            {
                var ok = g.Where(a => a.Status == AttemptStatus.Captured).ToList();
                return new BreakdownRow(g.Key, labeller(g.Key), g.Count(), ok.Count,
                    Rate(ok.Count, g.Count()), ok.Sum(a => a.AmountMinor));
            })
            .OrderByDescending(r => r.GmvMinor)
            .ThenByDescending(r => r.Attempts)
            .ToList();

    private static IReadOnlyList<FailureRow> Failures(List<AttemptRow> attempts)
    {
        var failed = attempts.Where(a => a.Status == AttemptStatus.Failed).ToList();
        if (failed.Count == 0)
            return [];

        return failed
            .GroupBy(a => a.ErrorUnifiedCode ?? "poyra.unknown")
            .Select(g => new FailureRow(g.Key, FailureLabel(g.Key), g.Count(), (double)g.Count() / failed.Count))
            .OrderByDescending(r => r.Count)
            .ToList();
    }

    private static IReadOnlyList<HourRow> Hours(List<AttemptRow> attempts)
        => Enumerable.Range(0, 24)
            .Select(hour =>
            {
                // TR saati: sunucu UTC yazar, işyeri kendi saatini sorar
                var slice = attempts.Where(a => a.CreatedAt.UtcDateTime.AddHours(3).Hour == hour).ToList();
                var ok = slice.Where(a => a.Status == AttemptStatus.Captured).ToList();
                return new HourRow(hour, slice.Count, ok.Count, ok.Sum(a => a.AmountMinor));
            })
            .ToList();

    private static IReadOnlyList<DayRow> Days(List<AttemptRow> attempts, DateOnly from, DateOnly to)
    {
        var byDay = attempts
            .GroupBy(a => DateOnly.FromDateTime(a.CreatedAt.UtcDateTime.AddHours(3)))
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<DayRow>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var slice = byDay.GetValueOrDefault(day, []);
            var ok = slice.Where(a => a.Status == AttemptStatus.Captured).ToList();
            rows.Add(new DayRow(day, slice.Count, ok.Count, ok.Sum(a => a.AmountMinor)));
        }

        return rows;
    }

    private static double Rate(int part, int total) => total == 0 ? 0 : (double)part / total;

    private static int? Median(List<int> values)
    {
        if (values.Count == 0)
            return null;

        values.Sort();
        return values[values.Count / 2];
    }

    private static string FailureLabel(string code) => code switch
    {
        "poyra.insufficient_funds" => "Limit yetersiz",
        "poyra.card_declined" => "Banka onaylamadı",
        "poyra.expired_card" => "Kartın süresi dolmuş",
        "poyra.invalid_card" => "Kart bilgisi hatalı",
        "poyra.transaction_not_permitted" => "İşleme izin yok",
        "poyra.limit_exceeded" => "İşlem limiti aşıldı",
        "poyra.three_ds_failed" => "3D doğrulama başarısız",
        "poyra.three_ds_timeout" => "3D zaman aşımı (müşteri tamamlamadı)",
        "poyra.issuer_unavailable" => "Kart bankasına ulaşılamadı",
        "poyra.connector_unavailable" => "POS'a ulaşılamadı",
        "poyra.callback_signature_invalid" => "Banka imzası doğrulanamadı",
        "poyra.processing_error" => "İşlem hatası",
        _ => code,
    };
}

public sealed class AnalyticsOverviewEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<AnalyticsOverviewDto>
{
    public override void Configure()
    {
        Get("/v1/analytics/overview");
        Description(x => x.WithTags("Analytics"));
        Summary(s => s.Summary =
            "Dönem özeti: başarı oranı, GMV, POS/taksit/kart programı kırılımları, "
            + "başarısızlık nedenleri, saat ve gün dağılımı (TR günü).");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(
            new AnalyticsOverviewQuery(Query<int?>("days", isRequired: false) ?? 30), ct), ct);
}
