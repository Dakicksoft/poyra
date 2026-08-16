using System.Text.Json;
using Poyra.Modules.Disputes.Domain;
using Poyra.Modules.Webhooks.Contracts;

namespace Poyra.Modules.Disputes.Infrastructure;

public interface IDisputeNotifier
{
    Task PublishAsync(Dispute dispute, string eventType, object payload, CancellationToken ct);
}

public sealed class DisputeWebhookNotifier(IWebhookFanout fanout) : IDisputeNotifier
{
    public Task PublishAsync(Dispute dispute, string eventType, object payload, CancellationToken ct)
        => fanout.FanOutAsync(eventType, JsonSerializer.Serialize(new
        {
            dispute_id = dispute.PublicId,
            payment_id = dispute.PaymentPublicId,
            stage = DisputeStageMap.ToDb[dispute.Stage],
            status = DisputeStatusMap.ToDb[dispute.Status],
            evidence_due_at = dispute.EvidenceDueAt,
            data = payload,
        }), ct);
}
