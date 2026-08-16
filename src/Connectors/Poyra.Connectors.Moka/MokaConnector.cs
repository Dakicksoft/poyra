using System.Net.Http.Json;
using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Moka;

/// <summary>
/// <b>Moka</b> ödeme kuruluşu.
///
/// <b>PCI kapsamı:</b> kart İŞYERİ tarafında toplanır; banka-hosted giriş yoktur.
/// Poyra'da 3DS'li direct akış, hosted akışta bu hesap atlanır.
///
/// <b>Dönüş doğrulaması:</b> Moka'nın callback'inde imza YOKTUR — ama bir işlem sorgu
/// ucu vardır. Tahsilat bu yüzden dönüşteki "resultCode" ile değil,
/// <c>GetDealerPaymentTrxDetailList</c> sorgusuyla kesinleşir: sunucudan okunan durum
/// tek doğru kaynaktır.
///
/// <b>⚠ SERTİFİKASYON DURUMU:</b> alan adları ve durum kodları canlı hesapla
/// doğrulanmadan üretime alınmamalı.
/// </summary>
public sealed class MokaConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "moka";
    public const string HttpClientName = "poyra-moka";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Moka — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "Servis adresi (ör. https://service.moka.com)"),
            new CredentialField("dealer_code", "Bayi kodu (DealerCode)"),
            new CredentialField("username", "API kullanıcı adı"),
            new CredentialField("password", "API şifresi", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Kart İŞYERİ formunda toplanır → PCI kapsamı. Banka-hosted giriş yoktur; "
               + "hosted akışta bu hesap atlanır. Callback'te imza yok: tahsilat "
               + "GetDealerPaymentTrxDetailList sorgusuyla kesinleşir. TODO(cert).");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => throw new ConnectorConfigurationException(
            "Moka banka-hosted kart girişini desteklemiyor; 3DS'li direct akış kullanın.");

    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        using var yanit = await GonderAsync(credentials, "PaymentDealer/DoDirectPaymentThreeD", new
        {
            PaymentDealerAuthentication = Kimlik(credentials),
            PaymentDealerRequest = new
            {
                CardHolderFullName = request.Card.HolderName ?? "POYRA MUSTERI",
                CardNumber = request.Card.Pan,
                ExpMonth = request.Card.ExpiryMonth.ToString("D2"),
                ExpYear = request.Card.ExpiryYear.ToString("D4"),
                CvcNumber = request.Card.Cvv,
                Amount = MokaMessages.Amount(request.AmountMinor),
                Currency = MokaMessages.Currency(request.Currency),
                InstallmentNumber = Math.Max(1, request.Installments),
                ClientIP = request.CustomerIp ?? "0.0.0.0",
                OtherTrxCode = request.OrderId,
                Description = request.Description ?? request.OrderId,
                IsPoolPayment = 0,
                IsTokenized = 0,
                IsPreAuth = 0,
                Software = "Poyra",
                ReturnHash = 1,
                RedirectType = 0,
                RedirectUrl = callbackUrl,
            },
        }, ct);

        var kok = yanit.RootElement;
        var veri = kok.TryGetProperty("Data", out var d) ? d : default;
        var adres = Metin(veri, "Url");

        if (string.IsNullOrWhiteSpace(adres))
            throw new ConnectorUnavailableException(
                $"Moka 3D adresi dönmedi: {Metin(kok, "ResultCode")} {Metin(kok, "ResultMessage")}");

        // Moka form değil hazır ADRES döner — GET yönlendirmesi (alan yok).
        return new HostedPaymentForm(adres, new Dictionary<string, string>(), Method: "GET");
    }

    /// <summary>
    /// Moka callback'i imzasızdır: buradaki hiçbir alan tahsilat kanıtı sayılamaz.
    /// Sonuç <see cref="CompleteHostedCallbackAsync"/> içindeki sunucu sorgusundan okunur.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, SiparisNo(form), null, null, null, null,
            UnifiedErrors.ProcessingError, form.GetValueOrDefault("resultCode"),
            "Moka dönüşü imzasızdır; tahsilat sunucu sorgusuyla kesinleştirilmelidir.");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var orderId = SiparisNo(form);

        using var yanit = await GonderAsync(credentials, "PaymentDealer/GetDealerPaymentTrxDetailList", new
        {
            PaymentDealerAuthentication = Kimlik(credentials),
            PaymentDealerRequest = new
            {
                OtherTrxCode = orderId,
                PaymentId = form.GetValueOrDefault("trxCode") ?? string.Empty,
            },
        }, ct);

        var kok = yanit.RootElement;
        var ayrinti = IlkOdemeAyrintisi(kok);

        // İki alan da tutmalı: PaymentStatus=2 (tamamlandı) VE TrxStatus=1 (onaylandı).
        // Yalnız birine bakmak, iptal edilmiş bir işlemi başarılı saymaya açık bırakırdı.
        var tamamlandi = Metin(ayrinti, "PaymentStatus") == "2" && Metin(ayrinti, "TrxStatus") == "1";

        if (!tamamlandi)
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                MokaMessages.UnifiedError(Metin(kok, "ResultCode")),
                Metin(kok, "ResultCode"), Metin(kok, "ResultMessage"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Metin(ayrinti, "AuthCode"),
            ConnectorTxnId: Metin(ayrinti, "VirtualPosOrderId") ?? form.GetValueOrDefault("trxCode"),
            MaskedPan: Metin(ayrinti, "CardNumber"),
            CardBank: Metin(ayrinti, "BankName"),
            UnifiedErrors.None, Metin(kok, "ResultCode"), null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, "PaymentDealer/DoVoid", new
        {
            PaymentDealerAuthentication = Kimlik(credentials),
            PaymentDealerRequest = new
            {
                VirtualPosOrderId = reference.ConnectorTxnId ?? string.Empty,
                OtherTrxCode = reference.OrderId,
                ClientIP = "0.0.0.0",
                VoidRefundReason = 2,
            },
        }, reference.ConnectorTxnId, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, "PaymentDealer/DoCreateRefundRequest", new
        {
            PaymentDealerAuthentication = Kimlik(credentials),
            PaymentDealerRequest = new
            {
                VirtualPosOrderId = request.ConnectorTxnId ?? string.Empty,
                OtherTrxCode = request.OrderId,
                Amount = MokaMessages.Amount(request.AmountMinor),
            },
        }, request.ConnectorTxnId, ct);

    // ---- İç yardımcılar --------------------------------------------------------

    private static object Kimlik(ConnectorCredentials credentials) => new
    {
        DealerCode = credentials.Require("dealer_code"),
        Username = credentials.Require("username"),
        Password = credentials.Require("password"),
        CheckKey = MokaMessages.CheckKey(
            credentials.Require("dealer_code"),
            credentials.Require("username"),
            credentials.Require("password")),
    };

    private static string SiparisNo(IReadOnlyDictionary<string, string> form)
        => form.GetValueOrDefault("OtherTrxCode")
           ?? form.GetValueOrDefault("otherTrxCode", string.Empty);

    private async Task<ConnectorOperationResult> IslemAsync(
        ConnectorCredentials credentials, string yol, object govde, string? txnId, CancellationToken ct)
    {
        using var yanit = await GonderAsync(credentials, yol, govde, ct);
        var kok = yanit.RootElement;
        var veri = kok.TryGetProperty("Data", out var d) ? d : default;

        var basarili = veri.ValueKind == JsonValueKind.Object
                       && veri.TryGetProperty("IsSuccessful", out var bayrak)
                       && bayrak.ValueKind == JsonValueKind.True;

        return basarili
            ? ConnectorOperationResult.Ok(txnId)
            : ConnectorOperationResult.Fail(
                MokaMessages.UnifiedError(Metin(kok, "ResultCode")),
                Metin(kok, "ResultCode"), Metin(kok, "ResultMessage"));
    }

    private async Task<JsonDocument> GonderAsync(
        ConnectorCredentials credentials, string yol, object govde, CancellationToken ct)
    {
        var adres = $"{credentials.Require("gateway_base").TrimEnd('/')}/{yol}";

        try
        {
            var istemci = httpClientFactory.CreateClient(HttpClientName);
            using var yanit = await istemci.PostAsync(adres, JsonContent.Create(govde), ct);
            var metin = await yanit.Content.ReadAsStringAsync(ct);

            if (!yanit.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"Moka {yol} → {(int)yanit.StatusCode}.");

            return JsonDocument.Parse(metin);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"Moka {yol} ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("Moka yanıtı JSON değil.", ex);
        }
    }

    /// <summary>Sorgu yanıtı liste döner; aradığımız tek işlem listedeki ilkidir.</summary>
    private static JsonElement IlkOdemeAyrintisi(JsonElement kok)
    {
        if (!kok.TryGetProperty("Data", out var veri)) return default;

        if (veri.TryGetProperty("PaymentDetail", out var tekil) && tekil.ValueKind == JsonValueKind.Object)
            return tekil;

        if (veri.ValueKind == JsonValueKind.Array && veri.GetArrayLength() > 0)
            return veri[0];

        return default;
    }

    private static string? Metin(JsonElement element, string ad)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(ad, out var deger)
            ? deger.ValueKind switch
            {
                JsonValueKind.String => deger.GetString(),
                JsonValueKind.Number => deger.ToString(),
                _ => null,
            }
            : null;
}
