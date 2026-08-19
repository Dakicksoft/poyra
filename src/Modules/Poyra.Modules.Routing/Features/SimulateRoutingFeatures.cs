using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Routing.Domain;
using Poyra.Modules.Routing.Dsl;
using Poyra.Modules.Routing.Infrastructure;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Routing.Features;

public sealed record SimulationChangeDto(
    string PaymentId,
    string FromAccount,
    string ToAccount,
    long? FromCostMinor,
    long? ToCostMinor,
    long? SavingMinor,
    string Reason);

public sealed record SimulationResultDto(
    int SampleSize,
    int ChangedCount,
    long CurrentCostMinor,
    long SimulatedCostMinor,
    long EstimatedSavingMinor, // > 0: yeni kural daha ucuz
    int CostUnknownCount, // anlaşma tanımsız — tasarrufa dahil edilmedi
    int UnroutableCount, // aday kural yol bulamazdı (taksit/uygunluk elemeleri) — confirm başarısız olurdu
    int ForcedCount, // hesap elle sabitlenmişti — kural bu işlemleri değiştiremez, replay dışı
    IReadOnlyList<SimulationChangeDto> Changes);

/// <param name="Days">Kaç günlük geçmiş oynatılacak (varsayılan 30).</param>
public sealed record SimulateRoutingRequest(JsonElement Document, int Days = 30, int Limit = 1000);

public sealed record SimulateRoutingQuery(JsonElement Document, int Days, int Limit)
    : IQuery<SimulationResultDto>;

public sealed class SimulateRoutingValidator : AbstractValidator<SimulateRoutingQuery>
{
    public SimulateRoutingValidator()
    {
        RuleFor(x => x.Days).InclusiveBetween(1, 365);
        RuleFor(x => x.Limit).InclusiveBetween(1, 5_000);
        RuleFor(x => x.Document).Must(BeParseable)
            .WithMessage("Kural dokümanı geçersiz — Dsl/RuleDocument şemasına uymalı.");
    }

    private static bool BeParseable(JsonElement document)
    {
        try
        {
            RuleDocument.Parse(document.GetRawText());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// "Geçen ayın işlemlerini bu kuralla yeniden oynat, farkı gör."
/// Gerçek geçmiş işlemler aday kuralda değerlendirilir; hangi POS'a giderlerdi ve
/// komisyon farkı ne olurdu hesaplanır. Hiçbir şey YAZILMAZ — salt okuma, yan etkisiz.
/// Not: tahmin, komisyon anlaşması tanımlı işlemler üzerinden yapılır; tanımsızlar
/// ayrıca sayılır (sessizce sıfır sayılmaz).
/// Bilinen sınırlar (tahmin, birebir replay değildir):
///  - Sağlık ve uygunluk elemeleri BUGÜNKÜ fotoğrafla yapılır — geçmişteki durum bilinmez.
///  - Eski kayıtlarda (karar-anı kartı RoutingResultJson'a yazılmadan önce) kart MaskedPan'dan
///    yaklaşıklanır; pencere kaydıkça bu dal kendiliğinden ölür.
///  - Direct akışın "konnektör direct/3DS sunmuyor" elemesi simüle edilmez: bu yetenek
///    tanımlayıcıda bayrak değil, çağrı anında konnektörün null dönmesiyle keşfedilir.
/// </summary>
public sealed class SimulateRoutingHandler(
    RoutingDbContext db,
    IConnectorAccountsDirectory accounts,
    ICommissionRateSource rates,
    IConnectorPerformanceSource performance,
    IHistoricPaymentSource history,
    IExecutionFeasibilitySource feasibility)
    : IQueryHandler<SimulateRoutingQuery, SimulationResultDto>
{
    public async Task<SimulationResultDto> Handle(SimulateRoutingQuery query, CancellationToken ct)
    {
        var candidateDocument = RuleDocument.Parse(query.Document.GetRawText());

        var active = await accounts.GetActiveAccountsAsync(ct);
        if (active.Count == 0)
            throw new PoyraException(409, "routing.no_account", "Aktif bağlantı hesabı yok.");

        // Motorla aynı eleme: Down her zaman, skipUnhealthy açıkken Degraded da rota dışı
        var eligible = RoutingEngine.FilterEligible(active, candidateDocument.Guards);
        if (eligible.Count == 0)
            throw new PoyraException(409, "routing.no_healthy_account",
                "Tüm hesaplar sağlıksız — aday kural hiçbir işlemi yönlendiremezdi.");

        var labels = active.ToDictionary(a => a.Id, a => a.Label);
        var since = DateTimeOffset.UtcNow.AddDays(-query.Days);
        var payments = await history.GetAsync(since, query.Limit, ct);

        // Sinyaller bir kez çekilir; taksit sayısına göre oran tablosu önbelleklenir
        var performanceByAccount = (await performance.GetAsync(RoutingEngine.PerformanceWindow, ct))
            .ToDictionary(p => p.ConnectorAccountId);
        var ratesByInstallment = new Dictionary<int, Dictionary<Guid, int>>();
        var feasibleByKey = new Dictionary<(int Installments, string? Program), IReadOnlySet<Guid>>();

        var changes = new List<SimulationChangeDto>();
        long currentCost = 0, simulatedCost = 0;
        var costUnknown = 0;
        var unroutable = 0;
        var forcedCount = 0;

        foreach (var payment in payments)
        {
            if (payment.Forced)
            {
                forcedCount++; // işyeri hesabı elle sabitledi — kural değişse de zorlamaya devam eder
                continue;
            }

            if (!ratesByInstallment.TryGetValue(payment.Installments, out var rateMap))
            {
                rateMap = (await rates.GetRatesAsync(payment.Installments, ct))
                    .ToDictionary(r => r.ConnectorAccountId, r => r.RateBps);
                ratesByInstallment[payment.Installments] = rateMap;
            }

            var candidates = eligible
                .Select(a => RoutingEngine.Enrich(a, payment.AmountMinor, rateMap, performanceByAccount))
                .ToList();

            // Ortak karar çekirdeği: kural → hacim bölüşümü → strateji — motor ne yaparsa o.
            // Seed gerçek intent id'si olduğundan bölüşüm kovası da motordakiyle birebir aynı düşer.
            var facts = new RoutingFacts(payment.Seed, payment.AmountMinor, payment.Currency,
                payment.Installments, payment.HourLocal, payment.Card);
            var decision = RoutingEngine.DecideCore(candidateDocument, facts, candidates);

            // Confirm döngüsüyle aynı yürüyüş: zincirin ilk MaxAttempts adayından, yürütme-anı
            // elemelerini (taksit şeması, konnektör çözümü) geçen İLKİ gerçek hedeftir.
            // Program, karar-anı kartından gelir — confirm'deki türetmeyle aynı kaynak.
            var key = (payment.Installments, payment.Card?.Program);
            if (!feasibleByKey.TryGetValue(key, out var feasible))
            {
                feasible = await feasibility.GetCapableAccountsAsync(key.Installments, key.Program, ct);
                feasibleByKey[key] = feasible;
            }

            var targetId = decision.AccountIds
                .Take(Math.Max(1, decision.MaxAttempts))
                .FirstOrDefault(feasible.Contains);
            if (targetId == Guid.Empty)
            {
                unroutable++; // aday kural bu işleme yol bulamazdı — confirm başarısız olurdu
                continue;
            }

            var reason = decision.Reason;

            var actualCost = rateMap.TryGetValue(payment.ActualAccountId, out var actualBps)
                ? (long?)Math.Round(payment.AmountMinor * (actualBps / 10_000m), 0, MidpointRounding.ToEven)
                : null;
            var newCost = candidates.FirstOrDefault(c => c.AccountId == targetId)?.ExpectedCostMinor;

            if (actualCost is { } a1 && newCost is { } n1)
            {
                currentCost += a1;
                simulatedCost += n1;
            }
            else
            {
                costUnknown++;
            }

            if (targetId != payment.ActualAccountId)
                changes.Add(new SimulationChangeDto(
                    payment.PaymentId,
                    labels.GetValueOrDefault(payment.ActualAccountId, "(kapatılmış hesap)"),
                    labels.GetValueOrDefault(targetId, "?"),
                    actualCost, newCost,
                    actualCost is { } a2 && newCost is { } n2 ? a2 - n2 : null,
                    reason));
        }

        return new SimulationResultDto(
            payments.Count,
            changes.Count,
            currentCost,
            simulatedCost,
            currentCost - simulatedCost,
            costUnknown,
            unroutable,
            forcedCount,
            changes.OrderByDescending(c => c.SavingMinor ?? 0).Take(100).ToList());
    }

}

public sealed class SimulateRoutingEndpoint(IDispatcher dispatcher)
    : Endpoint<SimulateRoutingRequest, SimulationResultDto>
{
    public override void Configure()
    {
        Post("/v1/routing/simulate");
        Description(x => x.WithTags("Routing"));
        Summary(s => s.Summary =
            "Aday kuralı geçmiş işlemler üzerinde oynatır; POS değişimlerini ve komisyon farkını döner (salt okuma).");
    }

    public override async Task HandleAsync(SimulateRoutingRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(
            new SimulateRoutingQuery(req.Document, req.Days, req.Limit), ct), ct);
}
