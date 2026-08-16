using Microsoft.Extensions.DependencyInjection;
using Poyra.Connectors.Abstractions;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Connectors.Infrastructure;

public sealed class ConnectorRegistry(IServiceProvider services, IReadOnlyList<string> keys)
{
    public IReadOnlyList<string> Keys { get; } = keys;

    public IPaymentConnector Get(string connectorKey)
        => services.GetKeyedService<IPaymentConnector>(connectorKey)
           ?? throw PoyraException.NotFound("connector.unknown", $"Bilinmeyen konnektör: '{connectorKey}'.");

    public bool Exists(string connectorKey)
        => services.GetKeyedService<IPaymentConnector>(connectorKey) is not null;

    public IReadOnlyList<ConnectorDescriptor> Catalog()
        => Keys.Select(k => Get(k).Descriptor).ToList();
}
