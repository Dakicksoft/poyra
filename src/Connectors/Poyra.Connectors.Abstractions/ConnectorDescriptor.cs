namespace Poyra.Connectors.Abstractions;

public enum ConnectorType
{
    BankVirtualPos,
    PaymentInstitution,
    Test,
}

public sealed record CredentialField(string Name, string Label, bool Secret = false, bool Required = true);

public sealed record ConnectorDescriptor(
    string Key,
    string DisplayName,
    ConnectorType Type,
    IReadOnlyList<CredentialField> CredentialFields,
    bool SupportsInstallments,
    bool SupportsVoid,
    bool SupportsRefund,
    string? Notes = null);
