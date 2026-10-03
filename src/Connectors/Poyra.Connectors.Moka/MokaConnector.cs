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
        using var response = await SendAsync(credentials, "PaymentDealer/DoDirectPaymentThreeD", new
        {
            PaymentDealerAuthentication = Authentication(credentials),
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

        var root = response.RootElement;
        var data = root.TryGetProperty("Data", out var d) ? d : default;
        var url = Text(data, "Url");

        if (string.IsNullOrWhiteSpace(url))
            throw new ConnectorUnavailableException(
                $"Moka 3D adresi dönmedi: {Text(root, "ResultCode")} {Text(root, "ResultMessage")}");

        // Moka form değil hazır ADRES döner — GET yönlendirmesi (alan yok).
        return new HostedPaymentForm(url, new Dictionary<string, string>(), Method: "GET");
    }

    /// <summary>
    /// Moka callback'i imzasızdır: buradaki hiçbir alan tahsilat kanıtı sayılamaz.
    /// Sonuç <see cref="CompleteHostedCallbackAsync"/> içindeki sunucu sorgusundan okunur.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, ReadOrderId(form), null, null, null, null,
            UnifiedErrors.ProcessingError, form.GetValueOrDefault("resultCode"),
            "Moka dönüşü imzasızdır; tahsilat sunucu sorgusuyla kesinleştirilmelidir.");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var orderId = ReadOrderId(form);

        using var response = await SendAsync(credentials, "PaymentDealer/GetDealerPaymentTrxDetailList", new
        {
            PaymentDealerAuthentication = Authentication(credentials),
            PaymentDealerRequest = new
            {
                OtherTrxCode = orderId,
                PaymentId = form.GetValueOrDefault("trxCode") ?? string.Empty,
            },
        }, ct);

        var root = response.RootElement;
        var detail = FirstPaymentDetail(root);

        // İki alan da tutmalı: PaymentStatus=2 (tamamlandı) VE TrxStatus=1 (onaylandı).
        // Yalnız birine bakmak, iptal edilmiş bir işlemi başarılı saymaya açık bırakırdı.
        var completed = Text(detail, "PaymentStatus") == "2" && Text(detail, "TrxStatus") == "1";

        if (!completed)
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                MokaMessages.UnifiedError(Text(root, "ResultCode")),
                Text(root, "ResultCode"), Text(root, "ResultMessage"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Text(detail, "AuthCode"),
            ConnectorTxnId: Text(detail, "VirtualPosOrderId") ?? form.GetValueOrDefault("trxCode"),
            MaskedPan: Text(detail, "CardNumber"),
            CardBank: Text(detail, "BankName"),
            UnifiedErrors.None, Text(root, "ResultCode"), null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, "PaymentDealer/DoVoid", new
        {
            PaymentDealerAuthentication = Authentication(credentials),
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
        => OperationAsync(credentials, "PaymentDealer/DoCreateRefundRequest", new
        {
            PaymentDealerAuthentication = Authentication(credentials),
            PaymentDealerRequest = new
            {
                VirtualPosOrderId = request.ConnectorTxnId ?? string.Empty,
                OtherTrxCode = request.OrderId,
                Amount = MokaMessages.Amount(request.AmountMinor),
            },
        }, request.ConnectorTxnId, ct);

    // ---- İç yardımcılar --------------------------------------------------------

    private static object Authentication(ConnectorCredentials credentials) => new
    {
        DealerCode = credentials.Require("dealer_code"),
        Username = credentials.Require("username"),
        Password = credentials.Require("password"),
        CheckKey = MokaMessages.CheckKey(
            credentials.Require("dealer_code"),
            credentials.Require("username"),
            credentials.Require("password")),
    };

    private static string ReadOrderId(IReadOnlyDictionary<string, string> form)
        => form.GetValueOrDefault("OtherTrxCode")
           ?? form.GetValueOrDefault("otherTrxCode", string.Empty);

    private async Task<ConnectorOperationResult> OperationAsync(
        ConnectorCredentials credentials, string path, object body, string? txnId, CancellationToken ct)
    {
        using var response = await SendAsync(credentials, path, body, ct);
        var root = response.RootElement;
        var data = root.TryGetProperty("Data", out var d) ? d : default;

        var succeeded = data.ValueKind == JsonValueKind.Object
                       && data.TryGetProperty("IsSuccessful", out var flag)
                       && flag.ValueKind == JsonValueKind.True;

        return succeeded
            ? ConnectorOperationResult.Ok(txnId)
            : ConnectorOperationResult.Fail(
                MokaMessages.UnifiedError(Text(root, "ResultCode")),
                Text(root, "ResultCode"), Text(root, "ResultMessage"));
    }

    private async Task<JsonDocument> SendAsync(
        ConnectorCredentials credentials, string path, object body, CancellationToken ct)
    {
        var url = $"{credentials.Require("gateway_base").TrimEnd('/')}/{path}";

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(url, JsonContent.Create(body), ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"Moka {path} → {(int)response.StatusCode}.");

            return JsonDocument.Parse(text);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"Moka {path} ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("Moka yanıtı JSON değil.", ex);
        }
    }

    /// <summary>Sorgu yanıtı liste döner; aradığımız tek işlem listedeki ilkidir.</summary>
    private static JsonElement FirstPaymentDetail(JsonElement root)
    {
        if (!root.TryGetProperty("Data", out var data)) return default;

        if (data.TryGetProperty("PaymentDetail", out var single) && single.ValueKind == JsonValueKind.Object)
            return single;

        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            return data[0];

        return default;
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null,
            }
            : null;
}
