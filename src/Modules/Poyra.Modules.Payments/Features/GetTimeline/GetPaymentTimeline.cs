using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Domain;
using Poyra.SharedKernel.Cqrs;

namespace Poyra.Modules.Payments.Features.GetTimeline;

public sealed record TimelineEventDto(
    string EventType, string Actor, JsonElement Payload, DateTimeOffset CreatedAt);

public sealed record TimelineAttemptDto(
    string Id,
    int AttemptNo,
    string ConnectorKey,
    Guid ConnectorAccountId,
    string Status,
    long AmountMinor,
    int Installments,
    string? MaskedPan,
    string? AuthCode,
    string? ConnectorTxnId,
    string? ErrorUnifiedCode,
    string? ErrorRawCode,
    string? ErrorRawMessage,
    DateTimeOffset? CapturedAt,
    DateTimeOffset CreatedAt);

public sealed record PaymentTimelineDto(
    string PaymentId,
    string Status,
    long AmountMinor,
    string Currency,
    int Installments,
    string? Description,
    JsonElement? RoutingResult,
    IReadOnlyList<TimelineAttemptDto> Attempts,
    IReadOnlyList<TimelineEventDto> Events,
    DateTimeOffset CreatedAt);

public sealed record GetPaymentTimelineQuery(string PublicId) : IQuery<PaymentTimelineDto?>;

public sealed class GetPaymentTimelineHandler(PaymentsDbContext db)
    : IQueryHandler<GetPaymentTimelineQuery, PaymentTimelineDto?>
{
    public async Task<PaymentTimelineDto?> Handle(GetPaymentTimelineQuery query, CancellationToken ct)
    {
        var intent = await db.PaymentIntents.AsNoTracking()
            .SingleOrDefaultAsync(p => p.PublicId == query.PublicId, ct);
        if (intent is null)
            return null;

        var attemptRows = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.PaymentIntentId == intent.Id)
            .OrderBy(a => a.AttemptNo)
            .ToListAsync(ct);

        var attempts = attemptRows
            .Select(a => new TimelineAttemptDto(
                a.PublicId, a.AttemptNo, a.ConnectorKey, a.ConnectorAccountId,
                AttemptStatusMap.ToDb[a.Status], a.AmountMinor, a.Installments,
                a.MaskedPan, a.AuthCode, a.ConnectorTxnId,
                a.ErrorUnifiedCode, a.ErrorRawCode, a.ErrorRawMessage, a.CapturedAt, a.CreatedAt))
            .ToList();

        var events = await db.PaymentEvents.AsNoTracking()
            .Where(e => e.PaymentIntentId == intent.Id)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.EventType, e.Actor, e.Payload, e.CreatedAt })
            .ToListAsync(ct);

        return new PaymentTimelineDto(
            intent.PublicId, PaymentStatusMap.ToDb[intent.Status], intent.AmountMinor, intent.Currency,
            intent.Installments, intent.Description,
            intent.RoutingResultJson is { } routing ? JsonDocument.Parse(routing).RootElement.Clone() : null,
            attempts,
            events.Select(e => new TimelineEventDto(
                e.EventType, e.Actor, JsonDocument.Parse(e.Payload).RootElement.Clone(), e.CreatedAt)).ToList(),
            intent.CreatedAt);
    }
}

public sealed class GetPaymentTimelineEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<PaymentTimelineDto>
{
    public override void Configure()
    {
        Get("/v1/payments/{id}/timeline");
        Description(x => x.WithTags("Payments"));
        Summary(s => s.Summary = "Ödemenin denemeleri, rota gerekçesi ve silinemez olay defteri.");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await dispatcher.Ask(new GetPaymentTimelineQuery(Route<string>("id")!), ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(result, ct);
    }
}
