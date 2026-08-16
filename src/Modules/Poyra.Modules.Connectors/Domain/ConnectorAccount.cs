using Poyra.Modules.Connectors.Contracts;
using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Connectors.Domain;

public enum ConnectorAccountStatus
{
    Active,
    Disabled, // silinmez — kapatılır, iz kalır
}

/// <summary>
/// İşyerinin bir banka/PSP sanal POS hesabı. Kimlik bilgileri AES-256-GCM ile şifreli
/// saklanır; panelden asla geri okunmaz (yalnız "değiştir"). DB'de DELETE yetkisi yoktur.
/// </summary>
public sealed class ConnectorAccount : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public required string ConnectorKey { get; init; }
    public required string Label { get; set; }
    public byte[] CredentialsEncrypted { get; set; } = [];
    public int Priority { get; set; } = 100;
    public bool TestMode { get; set; } = true;
    public ConnectorAccountStatus Status { get; set; } = ConnectorAccountStatus.Active;
    public ConnectorHealth Health { get; set; } = ConnectorHealth.Healthy;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ConnectorAccountSnapshot ToSnapshot()
        => new(Id, ConnectorKey, Label, Priority, TestMode, Health, Status == ConnectorAccountStatus.Active);
}
