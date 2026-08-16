using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.PayTR;

/// <summary>
/// <b>PayTR</b> ödeme kuruluşu.
///
/// <b>PCI kapsamı:</b> kart İŞYERİ tarafında toplanır ve sunucudan sunucuya iletilir.
/// Hosted akışta bu hesap aday listesinden düşer.
///
/// <b>Dönüş doğrulaması diğerlerinden farklı çalışır:</b> PayTR sonucu tarayıcı
/// yönlendirmesiyle DEĞİL, kendi sunucusundan bizim bildirim adresimize POST'layarak
/// bildirir ve bu bildirim <c>hash</c> ile imzalıdır. İmza <b>tutarı ve durumu</b>
/// kapsar. Tarayıcının döndüğü <c>merchant_ok_url</c> ise yalnız kullanıcı deneyimidir,
/// kanıt değildir — imzasız geldiği için burada zaten reddedilir.
///
/// <b>⚠ SERTİFİKASYON DURUMU / TODO(cert):</b> PayTR bildirime karşılık düz metin
/// <c>OK</c> yanıtı bekler; almazsa bildirimi tekrarlar. Bu, konnektörün değil callback
/// UÇ NOKTASININ sorumluluğu — sertifikasyondan önce API katmanında karşılanmalı.
/// </summary>
public sealed class PayTRConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "paytr";
    public const string HttpClientName = "poyra-paytr";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "PayTR — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "Servis adresi (ör. https://www.paytr.com)"),
            new CredentialField("merchant_id", "Mağaza no (merchant_id)"),
            new CredentialField("merchant_key", "Mağaza anahtarı (merchant_key)", Secret: true),
            new CredentialField("merchant_salt", "Mağaza tuzu (merchant_salt)", Secret: true),
            new CredentialField("test_mode", "Test modu (1/0)", Required: false),
        ],
        SupportsInstallments: true,
        SupportsVoid: false,
        SupportsRefund: true,
        Notes: "Kart İŞYERİ formunda toplanır → PCI kapsamı; banka-hosted giriş yoktur. "
               + "Sonuç, imzalı SUNUCU bildirimiyle gelir (tarayıcı dönüşü kanıt değildir). "
               + "İmza tutarı ve durumu kapsar. Tutar KURUŞ gider. TODO(cert): bildirim "
               + "ucu düz metin 'OK' döndürmeli, yoksa PayTR tekrar gönderir.");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => throw new ConnectorConfigurationException(
            "PayTR banka-hosted kart girişini desteklemiyor; 3DS'li direct akış kullanın.");

    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var merchantId = credentials.Require("merchant_id");
        var merchantKey = credentials.Require("merchant_key");
        var merchantSalt = credentials.Require("merchant_salt");
        var testMode = credentials.Get("test_mode") ?? "0";

        var tutar = PayTRMessages.Amount(request.AmountMinor);
        var ip = request.CustomerIp ?? "0.0.0.0";
        var taksit = request.Installments > 1 ? request.Installments.ToString() : "0";
        var eposta = "musteri@poyra.local";
        const string paraBirimi = "TL";

        var sepet = JsonSerializer.Serialize(new[]
        {
            new object[] { request.Description ?? "Siparis", tutar, 1 },
        });

        var alanlar = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant_id"] = merchantId,
            ["user_ip"] = ip,
            ["merchant_oid"] = request.OrderId,
            ["email"] = eposta,
            ["payment_type"] = "card",
            ["payment_amount"] = tutar,
            ["installment_count"] = taksit,
            ["currency"] = paraBirimi,
            ["test_mode"] = testMode,
            // 3DS'siz akış kullanılmıyor: kart doğrulaması bankada yapılmalı.
            ["non_3d"] = "0",
            ["cc_owner"] = request.Card.HolderName ?? "POYRA MUSTERI",
            ["card_number"] = request.Card.Pan,
            ["expiry_month"] = request.Card.ExpiryMonth.ToString("D2"),
            ["expiry_year"] = request.Card.ExpiryYear.ToString("D4")[^2..],
            ["cvv"] = request.Card.Cvv ?? string.Empty,
            ["merchant_ok_url"] = callbackUrl,
            ["merchant_fail_url"] = callbackUrl,
            ["user_name"] = request.Card.HolderName ?? "Poyra Musteri",
            ["user_address"] = "Bilinmiyor",
            ["user_phone"] = "0000000000",
            ["user_basket"] = sepet,
            ["paytr_token"] = PayTRMessages.RequestToken(
                merchantId, ip, request.OrderId, eposta, tutar, "card", taksit,
                paraBirimi, testMode, "0", merchantKey, merchantSalt),
        };

        var adres = $"{credentials.Require("gateway_base").TrimEnd('/')}/odeme";

        try
        {
            var istemci = httpClientFactory.CreateClient(HttpClientName);
            using var yanit = await istemci.PostAsync(adres, new FormUrlEncodedContent(alanlar), ct);
            var govde = await yanit.Content.ReadAsStringAsync(ct);

            if (!yanit.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"PayTR /odeme → {(int)yanit.StatusCode}.");

            var form = ConnectorHtml.FormuCikar(govde);
            if (form is not { } cikan)
                throw new ConnectorUnavailableException(
                    "PayTR 3D adımı için beklenen yönlendirme formu dönmedi.");

            return new HostedPaymentForm(cikan.ActionUrl, cikan.Fields);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException("PayTR /odeme ucuna ulaşılamadı.", ex);
        }
    }

    /// <summary>
    /// PayTR'ın imzalı SUNUCU bildirimi burada doğrulanır. İmza tutarı ve durumu
    /// kapsadığı için ayrı bir sorgu gerekmez — ama imzasız gelen hiçbir şey kabul
    /// edilmez (tarayıcı yönlendirmesi imzasızdır ve burada reddedilir).
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var orderId = form.GetValueOrDefault("merchant_oid", string.Empty);
        var durum = form.GetValueOrDefault("status");
        var tutar = form.GetValueOrDefault("total_amount", string.Empty);

        var beklenen = PayTRMessages.NotificationHash(
            orderId, durum ?? string.Empty, tutar,
            credentials.Require("merchant_key"), credentials.Require("merchant_salt"));

        if (!PayTRMessages.ImzaGecerli(form.GetValueOrDefault("hash"), beklenen))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, durum,
                "PayTR bildirim imzası doğrulanamadı (tarayıcı dönüşü kanıt değildir).");

        if (!PayTRMessages.Onaylandi(durum))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                PayTRMessages.UnifiedError(form.GetValueOrDefault("failed_reason_code")),
                durum, form.GetValueOrDefault("failed_reason_msg"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: form.GetValueOrDefault("payment_id"),
            ConnectorTxnId: form.GetValueOrDefault("payment_id"),
            MaskedPan: null,
            CardBank: null,
            UnifiedErrors.None, durum, null);
    }


    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => Task.FromResult(new ConnectorOperationResult(
            false, null, UnifiedErrors.NotSupported, null,
            "PayTR'da ayrı iptal ucu yoktur; iade kullanılmalı (TODO(cert))."));

    public async Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var merchantId = credentials.Require("merchant_id");
        var merchantKey = credentials.Require("merchant_key");
        var merchantSalt = credentials.Require("merchant_salt");
        var tutar = PayTRMessages.Amount(request.AmountMinor);

        var alanlar = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant_id"] = merchantId,
            ["merchant_oid"] = request.OrderId,
            ["return_amount"] = tutar,
            // İade imzası kendi alan listesini kullanır: sipariş + tutar + tuz.
            ["paytr_token"] = PayTRMessages.RefundToken(
                merchantId, request.OrderId, tutar, merchantKey, merchantSalt),
        };

        var adres = $"{credentials.Require("gateway_base").TrimEnd('/')}/odeme/iade";

        try
        {
            var istemci = httpClientFactory.CreateClient(HttpClientName);
            using var yanit = await istemci.PostAsync(adres, new FormUrlEncodedContent(alanlar), ct);
            var govde = await yanit.Content.ReadAsStringAsync(ct);

            if (!yanit.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"PayTR iade → {(int)yanit.StatusCode}.");

            using var belge = JsonDocument.Parse(govde);
            var durum = belge.RootElement.TryGetProperty("status", out var d) ? d.GetString() : null;

            return durum == "success"
                ? ConnectorOperationResult.Ok(request.ConnectorTxnId)
                : ConnectorOperationResult.Fail(
                    UnifiedErrors.ProcessingError,
                    belge.RootElement.TryGetProperty("err_no", out var k) ? k.ToString() : null,
                    belge.RootElement.TryGetProperty("err_msg", out var m) ? m.GetString() : null);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorUnavailableException("PayTR iade ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("PayTR iade yanıtı JSON değil.", ex);
        }
    }
}
