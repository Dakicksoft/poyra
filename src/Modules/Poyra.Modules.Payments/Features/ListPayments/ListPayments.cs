using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Domain;
using Poyra.SharedKernel.Cqrs;

namespace Poyra.Modules.Payments.Features.ListPayments;

public sealed record PaymentListItemDto(
    string Id,
    PaymentStatus Status,
    long AmountMinor,
    long? ChargedAmountMinor,
    string Currency,
    int Installments,
    string? Description,
    string? ConnectorKey,
    string? MaskedPan,
    string? LastErrorCode,
    DateTimeOffset CreatedAt);

public sealed record ListPaymentsQuery(string? Status, int Limit, DateTimeOffset? Before)
    : IQuery<IReadOnlyList<PaymentListItemDto>>;

public sealed class ListPaymentsHandler(PaymentsDbContext db)
    : IQueryHandler<ListPaymentsQuery, IReadOnlyList<PaymentListItemDto>>
{
    public async Task<IReadOnlyList<PaymentListItemDto>> Handle(ListPaymentsQuery query, CancellationToken ct)
    {
        var intents = db.PaymentIntents.AsNoTracking();

        if (query.Status is { } status && PaymentStatusMap.FromDb.TryGetValue(status, out var parsed))
            intents = intents.Where(p => p.Status == parsed);
        if (query.Before is { } before)
            intents = intents.Where(p => p.CreatedAt < before);

        return await intents
            .OrderByDescending(p => p.CreatedAt)
            .Take(Math.Clamp(query.Limit, 1, 200))
            .Select(p => new PaymentListItemDto(
                p.PublicId, p.Status, p.AmountMinor,
                db.PaymentAttempts.Where(a => a.PaymentIntentId == p.Id)
                    .OrderByDescending(a => a.AttemptNo).Select(a => (long?)a.AmountMinor).FirstOrDefault(),
                p.Currency, p.Installments, p.Description,
                db.PaymentAttempts.Where(a => a.PaymentIntentId == p.Id)
                    .OrderByDescending(a => a.AttemptNo).Select(a => a.ConnectorKey).FirstOrDefault(),
                db.PaymentAttempts.Where(a => a.PaymentIntentId == p.Id)
                    .OrderByDescending(a => a.AttemptNo).Select(a => a.MaskedPan).FirstOrDefault(),
                db.PaymentAttempts.Where(a => a.PaymentIntentId == p.Id)
                    .OrderByDescending(a => a.AttemptNo).Select(a => a.ErrorUnifiedCode).FirstOrDefault(),
                p.CreatedAt))
            .ToListAsync(ct);
    }
}

public sealed class ListPaymentsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<PaymentListItemDto>>
{
    public override void Configure()
    {
        Get("/v1/payments");
        Description(x => x.WithTags("Payments"));
        Summary(s => s.Summary = "Ödeme listesi (yeniden eskiye). Filtreler: status, limit, before (imleç).");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListPaymentsQuery(
            Query<string?>("status", isRequired: false),
            Query<int?>("limit", isRequired: false) ?? 50,
            Query<DateTimeOffset?>("before", isRequired: false)), ct), ct);
}
