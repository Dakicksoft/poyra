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
/// </summary>
public sealed class SimulateRoutingHandler(
    RoutingDbContext db,
    IConnectorAccountsDirectory accounts,
    ICommissionRateSource rates,
    IConnectorPerformanceSource performance,
    IHistoricPaymentSource history)
    : IQueryHandler<SimulateRoutingQuery, SimulationResultDto>
{
    public async Task<SimulationResultDto> Handle(SimulateRoutingQuery query, CancellationToken ct)
    {
        var candidateDocument = RuleDocument.Parse(query.Document.GetRawText());

        var active = await accounts.GetActiveAccountsAsync(ct);
        if (active.Count == 0)
            throw new PoyraException(409, "routing.no_account", "Aktif bağlantı hesabı yok.");

        var labels = active.ToDictionary(a => a.Id, a => a.Label);
        var since = DateTimeOffset.UtcNow.AddDays(-query.Days);
        var payments = await history.GetAsync(since, query.Limit, ct);

        // Sinyaller bir kez çekilir; taksit sayısına göre oran tablosu önbelleklenir
        var performanceByAccount = (await performance.GetAsync(RoutingEngine.PerformanceWindow, ct))
            .ToDictionary(p => p.ConnectorAccountId);
        var ratesByInstallment = new Dictionary<int, Dictionary<Guid, int>>();

        var changes = new List<SimulationChangeDto>();
        long currentCost = 0, simulatedCost = 0;
        var costUnknown = 0;

        foreach (var payment in payments)
        {
            if (!ratesByInstallment.TryGetValue(payment.Installments, out var rateMap))
            {
                rateMap = (await rates.GetRatesAsync(payment.Installments, ct))
                    .ToDictionary(r => r.ConnectorAccountId, r => r.RateBps);
                ratesByInstallment[payment.Installments] = rateMap;
            }

            var candidates = active
                .Where(a => a.Health != ConnectorHealth.Down)
                .Select(a =>
                {
                    long? cost = rateMap.TryGetValue(a.Id, out var bps)
                        ? (long)Math.Round(payment.AmountMinor * (bps / 10_000m), 0, MidpointRounding.ToEven)
                        : null;
                    performanceByAccount.TryGetValue(a.Id, out var stats);
                    var reliable = stats is { SampleSize: >= RoutingStrategies.MinimumSample };
                    return new RoutingCandidate(a.Id, a.Label, cost,
                        reliable ? stats!.AuthRate : null,
                        reliable && stats!.MedianLatencyMs > 0 ? stats.MedianLatencyMs : null);
                })
                .ToList();

            var (targetId, reason) = Decide(candidateDocument, payment, candidates);

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
            changes.OrderByDescending(c => c.SavingMinor ?? 0).Take(100).ToList());
    }

    /// <summary>Motorla AYNI karar sırası — simülasyon gerçeği yansıtsın diye ortak mantık.</summary>
    private static (Guid AccountId, string Reason) Decide(
        RuleDocument document, HistoricPayment payment, List<RoutingCandidate> candidates)
    {
        var facts = new RoutingFacts(payment.Seed, payment.AmountMinor, payment.Currency,
            payment.Installments, payment.HourLocal, payment.Card);

        var matched = RuleEvaluator.FirstMatch(document, facts);
        var strategy = matched?.Strategy ?? document.Strategy ?? RoutingStrategies.Priority;

        if (matched is not null && matched.Route.Count > 0)
        {
            var routed = candidates.FirstOrDefault(c =>
                matched.Route.Contains(c.Label, StringComparer.OrdinalIgnoreCase)
                || matched.Route.Contains(c.AccountId.ToString()));
            if (routed is not null)
                return (routed.AccountId, $"Kural: {matched.Reason ?? matched.Name ?? "adsız"}");
        }

        var ordered = RoutingStrategies.Order(candidates, strategy, document.Weights);
        var winner = ordered.FirstOrDefault() ?? candidates[0];
        return (winner.AccountId, strategy == RoutingStrategies.Priority
            ? "Öncelik sırası"
            : $"Strateji: {strategy}");
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
