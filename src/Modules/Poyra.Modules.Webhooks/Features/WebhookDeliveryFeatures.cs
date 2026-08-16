using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Webhooks.Domain;
using Poyra.Modules.Webhooks.Infrastructure;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Webhooks.Features;

public sealed record WebhookDeliveryResponse(
    Guid Id,
    Guid EndpointId,
    string EventType,
    DeliveryStatus Status,
    int AttemptCount,
    int? LastHttpStatus,
    string? LastError,
    DateTimeOffset? NextRetryAt,
    DateTimeOffset? DeliveredAt,
    Guid? ReplayOfId,
    DateTimeOffset CreatedAt);

internal static class WebhookDeliveryMap
{
    public static WebhookDeliveryResponse ToResponse(WebhookDelivery d)
        => new(d.Id, d.EndpointId, d.EventType, d.Status, d.AttemptCount, d.LastHttpStatus,
            d.LastError, d.NextRetryAt, d.DeliveredAt, d.ReplayOfId, d.CreatedAt);
}

public sealed record ListWebhookDeliveriesQuery(Guid? EndpointId, string? Status, int Limit)
    : IQuery<IReadOnlyList<WebhookDeliveryResponse>>;

public sealed class ListWebhookDeliveriesHandler(WebhooksDbContext db)
    : IQueryHandler<ListWebhookDeliveriesQuery, IReadOnlyList<WebhookDeliveryResponse>>
{
    public async Task<IReadOnlyList<WebhookDeliveryResponse>> Handle(
        ListWebhookDeliveriesQuery query, CancellationToken ct)
    {
        var deliveries = db.WebhookDeliveries.AsNoTracking();

        if (query.EndpointId is { } endpointId)
            deliveries = deliveries.Where(d => d.EndpointId == endpointId);
        if (query.Status is { } status && DeliveryStatusMap.FromDb.TryGetValue(status, out var parsed))
            deliveries = deliveries.Where(d => d.Status == parsed);

        return await deliveries
            .OrderByDescending(d => d.CreatedAt)
            .Take(Math.Clamp(query.Limit, 1, 200))
            .Select(d => WebhookDeliveryMap.ToResponse(d))
            .ToListAsync(ct);
    }
}

public sealed class ListWebhookDeliveriesEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<WebhookDeliveryResponse>>
{
    public override void Configure()
    {
        Get("/v1/webhook-deliveries");
        Description(x => x.WithTags("Webhooks"));
        Summary(s => s.Summary = "Teslim günlüğü (silinemez). Filtreler: endpointId, status, limit.");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await dispatcher.Ask(new ListWebhookDeliveriesQuery(
            Query<Guid?>("endpointId", isRequired: false),
            Query<string?>("status", isRequired: false),
            Query<int?>("limit", isRequired: false) ?? 50), ct);
        await Send.OkAsync(result, ct);
    }
}

public sealed record ReplayWebhookDeliveryCommand(Guid DeliveryId)
    : Poyra.SharedKernel.Cqrs.ICommand<WebhookDeliveryResponse>;

public sealed class ReplayWebhookDeliveryHandler(
    WebhooksDbContext db, TenantContext tenant, IBackgroundJobClient jobs)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ReplayWebhookDeliveryCommand, WebhookDeliveryResponse>
{
    public async Task<WebhookDeliveryResponse> Handle(ReplayWebhookDeliveryCommand command, CancellationToken ct)
    {
        var original = await db.WebhookDeliveries.AsNoTracking()
                           .SingleOrDefaultAsync(d => d.Id == command.DeliveryId, ct)
                       ?? throw PoyraException.NotFound("webhook_delivery.not_found", "Teslim kaydı bulunamadı.");

        var replay = original.CloneForReplay();
        db.WebhookDeliveries.Add(replay);
        await db.SaveChangesAsync(ct);

        var tenantId = tenant.TenantId;
        var replayId = replay.Id;
        jobs.Enqueue<WebhookDeliveryJob>(j => j.DeliverAsync(tenantId, replayId));

        return WebhookDeliveryMap.ToResponse(replay);
    }
}

public sealed class ReplayWebhookDeliveryEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<WebhookDeliveryResponse>
{
    public override void Configure()
    {
        Post("/v1/webhook-deliveries/{id}/replay");
        Description(x => x.WithTags("Webhooks"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new ReplayWebhookDeliveryCommand(Route<Guid>("id")), ct), ct);
}
