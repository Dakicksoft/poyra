using Poyra.Connectors.Abstractions;

namespace Poyra.Modules.Connectors.Contracts;

public enum ConnectorHealth
{
    Healthy,
    Degraded,
    Down,
}

public sealed record ConnectorAccountSnapshot(
    Guid Id,
    string ConnectorKey,
    string Label,
    int Priority, // küçük sayı = yüksek öncelik
    bool TestMode,
    ConnectorHealth Health,
    bool Active);

public interface IConnectorAccountsDirectory
{
    Task<IReadOnlyList<ConnectorAccountSnapshot>> GetActiveAccountsAsync(CancellationToken ct);
}

public sealed record ResolvedConnectorAccount(
    ConnectorAccountSnapshot Account,
    IPaymentConnector Connector,
    ConnectorCredentials Credentials);

public interface IConnectorGateway
{
    Task<ResolvedConnectorAccount> ResolveAsync(Guid connectorAccountId, CancellationToken ct);
}
