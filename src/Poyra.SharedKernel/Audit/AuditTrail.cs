namespace Poyra.SharedKernel.Audit;

public sealed record AuditEvent(
    string Actor,
    string Action,
    string ResourceType,
    string? ResourceId,
    string Summary,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? IpAddress = null);

public interface IAuditTrail
{
    Task RecordAsync(AuditEvent entry, CancellationToken ct);
}

public sealed class NullAuditTrail : IAuditTrail
{
    public Task RecordAsync(AuditEvent entry, CancellationToken ct) => Task.CompletedTask;
}
