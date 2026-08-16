using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Tenancy.Domain;

public sealed class SmsMessageRecord : IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid? TenantId { get; init; }

    /// <summary>E.164 biçiminde (+905321234567) — kuyruğa girmeden normalleştirilir.</summary>
    public required string ToPhone { get; init; }

    public required string Body { get; init; }
    public required string Purpose { get; init; }

    /// <summary>Tüketilen kredi — faturayı işyeri öder, panelde görünür olmalı.</summary>
    public int Segments { get; init; }

    public EmailStatus Status { get; set; } = EmailStatus.Pending;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>Sağlayıcının mesaj kimliği — teslimat sorgusu ve fatura eşleştirmesi için.</summary>
    public string? ProviderMessageId { get; set; }

    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public const int MaxAttempts = 5;
}
