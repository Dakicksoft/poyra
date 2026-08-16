namespace Poyra.Connectors.Abstractions;

public static class UnifiedErrors
{
    public const string CardDeclined = "poyra.card_declined";
    public const string InsufficientFunds = "poyra.insufficient_funds";
    public const string ExpiredCard = "poyra.expired_card";
    public const string InvalidCard = "poyra.invalid_card";
    public const string InvalidAmount = "poyra.invalid_amount";
    public const string NotPermitted = "poyra.transaction_not_permitted";
    public const string LimitExceeded = "poyra.limit_exceeded";
    public const string ThreeDsFailed = "poyra.three_ds_failed";
    public const string ThreeDsUnavailable = "poyra.three_ds_unavailable";
    public const string ThreeDsTimeout = "poyra.three_ds_timeout";
    public const string SignatureInvalid = "poyra.callback_signature_invalid";
    public const string IssuerUnavailable = "poyra.issuer_unavailable";
    public const string ConnectorUnavailable = "poyra.connector_unavailable";
    public const string VoidWindowClosed = "poyra.void_window_closed";
    public const string ProcessingError = "poyra.processing_error";
    public const string NotSupported = "poyra.not_supported";
    public const string None = "";

    /// <summary>Failover'a uygun (müşteri kartı henüz girilmemişken oluşan) hatalar.</summary>
    public static bool IsRetryableAtInitiate(string code)
        => code is ConnectorUnavailable or IssuerUnavailable or ProcessingError;
}

/// <summary>Konnektör yapılandırması hatalı (eksik alan, geçersiz uç).</summary>
public sealed class ConnectorConfigurationException(string message) : Exception(message);

/// <summary>Konnektöre ulaşılamadı / konnektör 5xx — initiate aşamasında failover tetikler.</summary>
public sealed class ConnectorUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
