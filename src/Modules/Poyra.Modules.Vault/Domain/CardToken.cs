using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Vault.Domain;

public sealed class CardToken : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }

    public string? CustomerRef { get; init; }

    public string PublicToken { get; private set; } = null!; // tok_… (DB computed)
    public byte[] CardEncrypted { get; init; } = [];
    public required string Fingerprint { get; init; } // HMAC-SHA256(PAN) hex
    public required string MaskedPan { get; init; }
    public required string Brand { get; init; }
    public required int ExpiryMonth { get; init; }
    public required int ExpiryYear { get; init; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsActive => DeletedAt is null;
}
