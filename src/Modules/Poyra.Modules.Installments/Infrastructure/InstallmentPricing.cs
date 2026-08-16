using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Installments.Contracts;
using Poyra.Modules.Installments.Domain;

namespace Poyra.Modules.Installments.Infrastructure;

public sealed class InstallmentPricing(InstallmentsDbContext db) : IInstallmentPricing
{
    public async Task<InstallmentPrice?> PriceAsync(
        Guid connectorAccountId, int installments, long amountMinor, string? program, CancellationToken ct)
    {
        if (installments <= 1)
            return new InstallmentPrice(amountMinor, 0, "-");

        var normalized = program?.Trim().ToLowerInvariant();
        var schemes = await db.InstallmentSchemes.AsNoTracking()
            .Where(s => s.Active
                        && s.ConnectorAccountId == connectorAccountId
                        && s.InstallmentCount == installments
                        && (s.Program == "*" || (normalized != null && s.Program == normalized)))
            .ToListAsync(ct);

        // Programa özel şema jokeri ezer (quote ile aynı kural)
        var scheme = schemes.OrderByDescending(s => s.Program != "*").FirstOrDefault();
        return scheme is null
            ? null
            : new InstallmentPrice(
                InstallmentMath.TotalWithRate(amountMinor, scheme.CustomerRateBps),
                scheme.CustomerRateBps,
                scheme.Program);
    }
}
