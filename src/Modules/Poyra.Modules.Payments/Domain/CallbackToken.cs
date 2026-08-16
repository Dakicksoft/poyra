using System.Buffers.Text;
using System.Security.Cryptography;
using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Payments.Domain;

public sealed class CallbackToken : IHasCreatedAt
{
    public string Token { get; init; } = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    public Guid TenantId { get; init; }
    public Guid PaymentIntentId { get; init; }
    public Guid PaymentAttemptId { get; init; }
    public required string ConnectorKey { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static CallbackToken Create(
        PaymentIntent intent, PaymentAttempt attempt, string connectorKey, DateTimeOffset expiresAt)
        => new()
        {
            TenantId = intent.TenantId,
            PaymentIntentId = intent.Id,
            PaymentAttemptId = attempt.Id,
            ConnectorKey = connectorKey,
            ExpiresAt = expiresAt,
        };
}
