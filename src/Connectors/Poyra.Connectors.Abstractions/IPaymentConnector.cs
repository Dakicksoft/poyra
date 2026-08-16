namespace Poyra.Connectors.Abstractions;


public interface IPaymentConnector
{
    string Key { get; }
    ConnectorDescriptor Descriptor { get; }

    /// <summary>Müşterinin tarayıcısının bankaya POST edeceği formu üretir (3d_pay_hosting benzeri).</summary>
    Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct);

    /// <summary>Banka dönüş POST'unu doğrular (imza/hash) ve normalize eder.</summary>
    HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials);

    /// <summary>
    /// Dönüşü tamamlar. Varsayılan: senkron imza doğrulaması. Sonucu redirect
    /// parametresinden değil sunucudan sorgulaması gereken protokoller (ör. PayFlex
    /// Ortak Ödeme Sayfası) bunu override eder.
    /// </summary>
    Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
        => Task.FromResult(ParseAndValidateCallback(form, credentials));

    /// <summary>
    /// Hafif sağlık yoklaması (canary). null = bu konnektör yoklama desteklemiyor
    /// (sağlık durumu elle/işlem sonuçlarından yönetilir).
    /// </summary>
    Task<ConnectorProbeResult?> ProbeAsync(ConnectorCredentials credentials, CancellationToken ct)
        => Task.FromResult<ConnectorProbeResult?>(null);

    /// <summary>
    /// KART VERİSİ POYRA'DAN GEÇER (PCI kapsamı!): kendi formumuz/kayıtlı kart
    /// ile tek adımda satış. null = bu konnektör direct desteklemiyor (rota adayı atlanır).
    /// CVV asla saklanmaz/loglanmaz; istek nesnesi konnektör çağrısından sonra düşer.
    /// </summary>
    Task<DirectAuthorizeResult?> AuthorizeDirectAsync(
        DirectPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => Task.FromResult<DirectAuthorizeResult?>(null);

    /// <summary>
    /// 3DS'Lİ DIRECT: kart bizim formumuzda toplanır (PCI kapsamı) ama kimlik doğrulama
    /// bankada yapılır — müşteri bankanın 3DS sayfasına yönlendirilir, dönüş callback'e düşer.
    /// Hosted akıştan farkı KARTIN NEREDE GİRİLDİĞİdir; ödeme sayfası bizde kalır, dönüşüm
    /// yüksektir ve chargeback sorumluluğu 3DS ile bankaya geçer.
    ///
    /// Posnet'in OOS (Out Of Store) akışı tam olarak budur ve YKB'de tek 3DS seçeneğidir.
    /// Dönüş <see cref="CompleteHostedCallbackAsync"/> ile tamamlanır (çoğu protokolde
    /// ikinci bir sunucu çağrısı gerekir — imzayı doğrula, sonra finansallaştır).
    ///
    /// null = konnektör 3DS'li direct desteklemiyor (rota adayı atlanır).
    /// </summary>
    Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials, CancellationToken ct)
        => Task.FromResult<HostedPaymentForm?>(null);

    /// <summary>Gün sonu öncesi iptal — komisyon doğmaz.</summary>
    Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct);

    /// <summary>Gün sonu sonrası (kısmi) iade.</summary>
    Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct);
}

/// <param name="OrderId">Bankaya giden sipariş no — bizim attempt public id'miz (att_…).</param>
public sealed record HostedPaymentRequest(
    string OrderId,
    long AmountMinor,
    string Currency,
    int Installments,
    string CallbackUrl,
    string? Description,
    string? CustomerIp);

/// <param name="Fields">Müşterinin tarayıcısının bankaya göndereceği alanlar (POST akışı).</param>
/// <param name="ConnectorState">
/// Konnektörün DÖNÜŞTE ihtiyaç duyacağı ama müşteriye GÖSTERİLMEYECEK durum.
/// Deneme kaydına yazılır ve callback'te forma harmanlanarak konnektöre geri verilir.
///
/// Neden: bazı sağlayıcılar (Adyen Pay by Link) sonucu sorgulamak için kendi
/// ürettikleri bir kimliği ister ama o kimliği dönüş adresine EKLEMEZ. Kimliği
/// tarayıcıya emanet etmek de olmaz — kurcalanabilir ve başka bir işlemin sonucu
/// okutulabilirdi.
/// </param>
public sealed record HostedPaymentForm(
    string ActionUrl,
    IReadOnlyDictionary<string, string> Fields,
    string Method = "POST",
    IReadOnlyDictionary<string, string>? ConnectorState = null);

public sealed record HostedCallbackResult(
    bool Success,
    string OrderId,
    string? AuthCode,
    string? ConnectorTxnId,
    string? MaskedPan,
    string? CardBank,
    string UnifiedCode,
    string? RawCode,
    string? RawMessage);

public sealed record ConnectorReference(string OrderId, string? ConnectorTxnId);

public sealed record ConnectorProbeResult(bool Healthy, string? Detail = null);

public sealed record CardData(string Pan, int ExpiryMonth, int ExpiryYear, string? HolderName, string? Cvv)
{
    public override string ToString() => $"CardData({CardNumbers.Mask(Pan)})";
}

public sealed record DirectPaymentRequest(
    string OrderId,
    long AmountMinor,
    string Currency,
    int Installments,
    CardData Card,
    string? Description,
    string? CustomerIp);

public sealed record DirectAuthorizeResult(
    bool Success,
    string? AuthCode,
    string? ConnectorTxnId,
    string? MaskedPan,
    string UnifiedCode,
    string? RawCode,
    string? RawMessage);

public sealed record ConnectorRefundRequest(
    string OrderId, string? ConnectorTxnId, long AmountMinor, string Currency);

public sealed record ConnectorOperationResult(
    bool Success,
    string? ConnectorTxnId,
    string? UnifiedCode,
    string? RawCode,
    string? RawMessage)
{
    public static ConnectorOperationResult Ok(string? txnId = null) => new(true, txnId, null, null, null);

    public static ConnectorOperationResult Fail(string unifiedCode, string? rawCode, string? rawMessage)
        => new(false, null, unifiedCode, rawCode, rawMessage);
}
