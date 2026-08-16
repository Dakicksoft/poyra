using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Tenancy.Domain;

public enum EmailStatus
{
    Pending,
    Sent,
    Failed,
}

public static class EmailStatusMap
{
    public static readonly IReadOnlyDictionary<EmailStatus, string> ToDb =
        new Dictionary<EmailStatus, string>
        {
            [EmailStatus.Pending] = "pending",
            [EmailStatus.Sent] = "sent",
            [EmailStatus.Failed] = "failed",
        };

    public static readonly IReadOnlyDictionary<string, EmailStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

public sealed class EmailMessageRecord : IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid? TenantId { get; init; }
    public required string ToEmail { get; init; }
    public required string Subject { get; init; }
    public required string BodyHtml { get; set; }
    public required string BodyText { get; set; }
    public required string Purpose { get; init; }
    public EmailStatus Status { get; set; } = EmailStatus.Pending;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public const int MaxAttempts = 5;
}
