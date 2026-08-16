namespace Poyra.Connectors.Abstractions;

/// <summary>Çözülmüş (düz metin) kimlik bilgileri — yalnız bellek içinde, asla loglanmaz.</summary>
public sealed class ConnectorCredentials(IReadOnlyDictionary<string, string> values)
{
    public IReadOnlyDictionary<string, string> Values { get; } = values;

    public string Require(string name)
        => Values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ConnectorConfigurationException($"Zorunlu kimlik alanı eksik: '{name}'.");

    public string? Get(string name)
        => Values.TryGetValue(name, out var value) ? value : null;
}
