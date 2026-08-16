using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Modules.Connectors.Infrastructure;

/// <summary>
/// Test/sandbox bankası. Gerçek hosted akışı taklit eder: initiate bir form döner,
/// "banka" (test istemcisi) formu callback'e POST'lar. Sonuç tutarla yönetilir:
/// kuruş %100 == 99 → kart reddi (05), %100 == 98 → 3DS başarısız, diğerleri onay.
/// fail_initiate=true kimlik alanı, failover senaryoları için initiate'i düşürür.
/// </summary>
public sealed class MockBankConnector : IPaymentConnector
{
    public const string ConnectorKey = "mockbank";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Mock Banka (sandbox)",
        ConnectorType.Test,
        [
            new CredentialField("secret", "İmza sırrı", Secret: true),
            new CredentialField("fail_initiate", "Initiate'i düşür (failover testi)", Required: false),
            new CredentialField("no_3ds_direct", "3DS'li direct'i kapat (destek yok testi)", Required: false),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true);

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        if (credentials.Get("fail_initiate") == "true")
            throw new ConnectorUnavailableException("Mock banka kapalı (fail_initiate=true).");

        var outcome = (request.AmountMinor % 100) switch
        {
            99 => "declined05",
            98 => "3dsfail",
            _ => "approved",
        };

        var fields = new Dictionary<string, string>
        {
            ["mb_order"] = request.OrderId,
            ["mb_amount"] = request.AmountMinor.ToString(),
            ["mb_outcome"] = outcome,
            ["mb_sig"] = Sign(credentials.Require("secret"), request.OrderId, request.AmountMinor.ToString(), outcome),
        };

        // "Banka sayfası" yok: form doğrudan bizim callback'imize post eder.
        return Task.FromResult(new HostedPaymentForm(request.CallbackUrl, fields));
    }

    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var order = form.GetValueOrDefault("mb_order", "");
        var amount = form.GetValueOrDefault("mb_amount", "");
        var outcome = form.GetValueOrDefault("mb_outcome", "");
        var sig = form.GetValueOrDefault("mb_sig", "");

        if (Sign(credentials.Require("secret"), order, amount, outcome) != sig)
            return new HostedCallbackResult(false, order, null, null, null, null,
                UnifiedErrors.SignatureInvalid, null, "Mock imza doğrulaması başarısız.");

        return outcome switch
        {
            "approved" => new HostedCallbackResult(true, order, "MOCK01", $"mocktxn-{order}",
                "540667******1234", "Mock Bankası", "", "00", null),
            "3dsfail" => new HostedCallbackResult(false, order, null, null, null, null,
                UnifiedErrors.ThreeDsFailed, "mdStatus=0", "3D doğrulama başarısız"),
            _ => new HostedCallbackResult(false, order, null, null, null, null,
                UnifiedErrors.CardDeclined, "05", "Do not honor"),
        };
    }

    /// <summary>
    /// Direct (kart Poyra'da) simülasyonu: fail_initiate=true → erişilemez (failover tetikler);
    /// kuruş %100 == 99 → kart reddi (failover DURMALI), %100 == 97 → issuer_unavailable
    /// (retryable — failover sonraki hesaba geçer), diğerleri onay.
    /// </summary>
    public Task<DirectAuthorizeResult?> AuthorizeDirectAsync(
        DirectPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        if (credentials.Get("fail_initiate") == "true")
            throw new ConnectorUnavailableException("Mock banka kapalı (fail_initiate=true).");

        if (!CardNumbers.IsValidLuhn(request.Card.Pan))
            return Task.FromResult<DirectAuthorizeResult?>(new DirectAuthorizeResult(
                false, null, null, CardNumbers.Mask(request.Card.Pan),
                UnifiedErrors.InvalidCard, "14", "Geçersiz kart numarası"));

        return Task.FromResult<DirectAuthorizeResult?>((request.AmountMinor % 100) switch
        {
            99 => new DirectAuthorizeResult(false, null, null, CardNumbers.Mask(request.Card.Pan),
                UnifiedErrors.CardDeclined, "05", "Do not honor"),
            97 => new DirectAuthorizeResult(false, null, null, CardNumbers.Mask(request.Card.Pan),
                UnifiedErrors.IssuerUnavailable, "91", "Issuer unavailable"),
            _ => new DirectAuthorizeResult(true, "MOCKD1", $"mockdirect-{request.OrderId}",
                CardNumbers.Mask(request.Card.Pan), "", "00", null),
        });
    }

    /// <summary>
    /// 3DS'li direct (OOS benzeri) simülasyonu: kart burada toplanır, banka imzalı bir paket
    /// üretir, müşteri onunla 3DS sayfasına gider. Formda KART VERİSİ YOKTUR — Posnet OOS'ta
    /// da öyledir ve testler bunu doğrular. no_3ds_direct=true olan hesap desteklemiyor sayılır.
    /// </summary>
    public Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials, CancellationToken ct)
    {
        if (credentials.Get("no_3ds_direct") == "true")
            return Task.FromResult<HostedPaymentForm?>(null);

        if (credentials.Get("fail_initiate") == "true")
            throw new ConnectorUnavailableException("Mock banka kapalı (fail_initiate=true).");

        if (!CardNumbers.IsValidLuhn(request.Card.Pan))
            throw new ConnectorConfigurationException("Geçersiz kart numarası (Luhn).");

        var outcome = (request.AmountMinor % 100) switch
        {
            99 => "declined05",
            98 => "3dsfail",
            _ => "approved",
        };

        // "Bankanın imzalı paketi": kart yerine yalnız sipariş+tutar+sonuç taşır
        var fields = new Dictionary<string, string>
        {
            ["mb_order"] = request.OrderId,
            ["mb_amount"] = request.AmountMinor.ToString(),
            ["mb_outcome"] = outcome,
            ["mb_sig"] = Sign(credentials.Require("secret"), request.OrderId, request.AmountMinor.ToString(), outcome),
        };

        return Task.FromResult<HostedPaymentForm?>(new HostedPaymentForm(callbackUrl, fields));
    }

    public Task<ConnectorProbeResult?> ProbeAsync(ConnectorCredentials credentials, CancellationToken ct)
        => Task.FromResult<ConnectorProbeResult?>(credentials.Get("fail_initiate") == "true"
            ? new ConnectorProbeResult(false, "fail_initiate=true")
            : new ConnectorProbeResult(true));

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => Task.FromResult(ConnectorOperationResult.Ok($"mockvoid-{reference.OrderId}"));

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => Task.FromResult(ConnectorOperationResult.Ok($"mockrefund-{request.OrderId}"));

    private static string Sign(string secret, params string[] parts)
        => Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(string.Join("|", parts))));
}
