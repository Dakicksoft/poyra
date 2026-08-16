using Poyra.Connectors.Abstractions;

namespace Poyra.Modules.Vault.Contracts;

public sealed record VaultedCard(string Token, CardData Card, string MaskedPan, string Brand);

public interface ICardVault
{
    Task<VaultedCard?> ResolveAsync(string token, CancellationToken ct);
}

/// <param name="MaskedPan">İlk 6 + son 4 — PCI dışıdır, müşteri ekranında gösterilebilir.</param>
public sealed record CustomerCard(
    string Token, string MaskedPan, string Brand, int ExpiryMonth, int ExpiryYear, bool Removed);

public interface ICustomerCardSource
{
    Task<IReadOnlyList<CustomerCard>> GetCardsAsync(string customerRef, CancellationToken ct);
}
