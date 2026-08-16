using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Vault.Contracts;

namespace Poyra.Modules.Vault.Infrastructure;

public sealed class CustomerCardSource(VaultDbContext db) : ICustomerCardSource
{
    public async Task<IReadOnlyList<CustomerCard>> GetCardsAsync(string customerRef, CancellationToken ct)
        => await db.CardTokens.AsNoTracking()
            .Where(c => c.CustomerRef == customerRef)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new CustomerCard(
                c.PublicToken, c.MaskedPan, c.Brand, c.ExpiryMonth, c.ExpiryYear, c.DeletedAt != null))
            .ToListAsync(ct);
}
