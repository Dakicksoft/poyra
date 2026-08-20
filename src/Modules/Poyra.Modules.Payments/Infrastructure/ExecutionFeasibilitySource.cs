using Poyra.Connectors.Abstractions;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Installments.Contracts;
using Poyra.Modules.Routing.Contracts;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Payments.Infrastructure;

/// <summary>
/// Confirm döngüsünün yürütme-anı elemelerini simülatöre açar (bkz. ConfirmPayment.Handle):
/// çözülemeyen hesap, taksitte SupportsInstallments=false konnektör ve taksit şeması/fiyatı
/// tanımsız hesap "yapamaz" sayılır. Kaynaklar confirm ile AYNI portlardır (gateway + pricing) —
/// ayrı bir kural kopyası yoktur, elemeler değişirse iki taraf birlikte değişir.
/// </summary>
public sealed class ExecutionFeasibilitySource(
    IConnectorAccountsDirectory accounts,
    IConnectorGateway gateway,
    IInstallmentPricing pricing) : IExecutionFeasibilitySource
{
    public async Task<IReadOnlySet<Guid>> GetCapableAccountsAsync(
        int installments, string? program, CancellationToken ct)
    {
        var capable = new HashSet<Guid>();
        foreach (var account in await accounts.GetActiveAccountsAsync(ct))
        {
            ResolvedConnectorAccount resolved;
            try
            {
                resolved = await gateway.ResolveAsync(account.Id, ct);
            }
            catch (PoyraException)
            {
                continue; // confirm de bu hesabı atlardı — hesap bu arada kapatılmış olabilir
            }

            if (installments > 1 && !resolved.Connector.Descriptor.SupportsInstallments)
                continue;

            // Şema varlığı tutara bağlı değildir; tutar yalnız müşteri toplamını etkiler
            if (await pricing.PriceAsync(account.Id, installments, amountMinor: 100, program, ct) is null)
                continue;

            capable.Add(account.Id);
        }

        return capable;
    }
}
