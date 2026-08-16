using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Routing.Domain;

public sealed class RoutingRule : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public required string Name { get; set; }
    public int Version { get; init; } = 1;
    /// <summary>RuleDocument JSON'u (jsonb) — şema: Dsl/RuleDocument.cs</summary>
    public required string Document { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
