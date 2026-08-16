using Microsoft.EntityFrameworkCore;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Connectors.Domain;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Security;

namespace Poyra.Modules.Connectors.Infrastructure;

/// <summary>Hesabı yükler (RLS + filtre işyerine sabitler), kimlikleri çözer, konnektörü bağlar.</summary>
public sealed class ConnectorGateway(
    ConnectorsDbContext db, ConnectorRegistry registry, ICredentialProtector protector) : IConnectorGateway
{
    public async Task<ResolvedConnectorAccount> ResolveAsync(Guid connectorAccountId, CancellationToken ct)
    {
        var account = await db.ConnectorAccounts.AsNoTracking()
                          .SingleOrDefaultAsync(a => a.Id == connectorAccountId, ct)
                      ?? throw PoyraException.NotFound("connector_account.not_found", "Bağlantı hesabı bulunamadı.");

        if (account.Status != ConnectorAccountStatus.Active)
            throw new PoyraException(409, "connector_account.disabled", $"'{account.Label}' hesabı devre dışı.");

        var connector = registry.Get(account.ConnectorKey);
        var credentials = new ConnectorCredentials(protector.Unprotect(account.CredentialsEncrypted));

        return new ResolvedConnectorAccount(account.ToSnapshot(), connector, credentials);
    }
}

public sealed class ConnectorAccountsDirectory(ConnectorsDbContext db) : IConnectorAccountsDirectory
{
    public async Task<IReadOnlyList<ConnectorAccountSnapshot>> GetActiveAccountsAsync(CancellationToken ct)
        => await db.ConnectorAccounts.AsNoTracking()
            .Where(a => a.Status == ConnectorAccountStatus.Active)
            .OrderBy(a => a.Priority).ThenBy(a => a.CreatedAt)
            .Select(a => a.ToSnapshot())
            .ToListAsync(ct);
}
