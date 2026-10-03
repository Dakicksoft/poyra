using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.AhlPay;

/// <summary>
/// <b>AHL Pay</b> ödeme kuruluşu.
///
/// <b>PCI kapsamı:</b> kart İŞYERİ tarafında toplanır ve sunucudan sunucuya iletilir;
/// banka-hosted giriş yoktur. Hosted akışta bu hesap aday listesinden düşer.
///
/// <b>Dönüş doğrulaması:</b> callback bir <c>responseHash</c> alanı taşıyor ama
/// sağlayıcının örnek yanıtında bu alan <c>null</c>'dur ve formülü belgelenmemiştir.
/// Bu yüzden dönüşe hiç güvenilmez: tahsilat, Bearer belirteçli <c>PaymentInquiry</c>
/// çağrısıyla SUNUCUDAN okunur (Moka ve Tami'deki desen).
///
/// <b>⚠ SERTİFİKASYON DURUMU:</b> istek hash formülü sağlayıcıdan alınmalı
/// (<see cref="AhlPayMessages.RequestHash"/> şu an yer tutucu).
/// </summary>
public sealed class AhlPayConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "ahlpay";
    public const string HttpClientName = "poyra-ahlpay";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "AHL Pay — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "Servis adresi (ör. https://testahlsanalpos.ahlpay.com.tr)"),
            new CredentialField("merchant_id", "Üye işyeri no (merchantId)"),
            new CredentialField("member_id", "Üye no (memberId)"),
            new CredentialField("user_code", "API kullanıcı e-postası (userCode)"),
            new CredentialField("password", "API kullanıcı şifresi", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Kart İŞYERİ formunda toplanır → PCI kapsamı; banka-hosted giriş yoktur. "
               + "Dönüş imzası belgelenmemiş (örnekte null) — tahsilat PaymentInquiry ile "
               + "sunucudan doğrulanır. TODO(cert): istek hash formülü alınmalı.");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => throw new ConnectorConfigurationException(
            "AHL Pay banka-hosted kart girişini desteklemiyor; 3DS'li direct akış kullanın.");

    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var random = AhlPayMessages.RandomNonce();

        using var response = await SendAsync(credentials, "api/Payment/Payment3d", new
        {
            cardNumber = request.Card.Pan,
            expiryDateMonth = request.Card.ExpiryMonth.ToString("D2"),
            expiryDateYear = request.Card.ExpiryYear.ToString("D4"),
            cvv = request.Card.Cvv,
            cardHolderName = request.Card.HolderName ?? "POYRA MUSTERI",
            merchantId = ParseInt(credentials.Require("merchant_id")),
            memberId = ParseInt(credentials.Require("member_id")),
            userCode = credentials.Require("user_code"),
            totalAmount = AhlPayMessages.Amount(request.AmountMinor),
            txnType = "Auth",
            // Tek çekimde "0" gider (sağlayıcının örneğinde de öyle), "1" değil.
            installmentCount = request.Installments > 1 ? request.Installments.ToString() : "0",
            currency = "949",
            orderId = request.OrderId,
            rnd = random,
            hash = AhlPayMessages.RequestHash(random),
            description = request.Description ?? request.OrderId,
            requestIp = request.CustomerIp ?? "0.0.0.0",
            webUrl = callbackUrl,
            okUrl = callbackUrl,
            failUrl = callbackUrl,
        }, ct);

        var root = response.RootElement;
        if (!Flag(root, "isSuccess"))
            throw new ConnectorUnavailableException(
                $"AHL Pay 3D başlatma reddetti: {Text(root, "errorCode")} {Text(root, "message")}");

        var form = ConnectorHtml.ExtractForm(Text(root, "data") ?? string.Empty);
        if (form is not { } extracted)
            throw new ConnectorUnavailableException("AHL Pay 3D yanıtında beklenen form yok.");

        return new HostedPaymentForm(extracted.ActionUrl, extracted.Fields);
    }

    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, form.GetValueOrDefault("orderId", string.Empty), null, null, null, null,
            UnifiedErrors.ProcessingError, form.GetValueOrDefault("responseCode"),
            "AHL Pay dönüşü PaymentInquiry ile kesinleştirilmelidir.");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var orderId = form.GetValueOrDefault("orderId", string.Empty);
        var random = AhlPayMessages.RandomNonce();

        using var query = await SendAsync(credentials, "api/Payment/PaymentInquiry", new
        {
            merchantId = ParseInt(credentials.Require("merchant_id")),
            memberId = ParseInt(credentials.Require("member_id")),
            orderId,
            rnd = random,
            hash = AhlPayMessages.RequestHash(random),
        }, ct);

        var root = query.RootElement;
        var data = root.TryGetProperty("data", out var d) ? d : default;
        var status = Text(data, "txnStatus");

        // İki koşul birden: sorgu başarılı VE işlem durumu tahsilat anlamına gelmeli.
        // Yalnız isSuccess'e bakmak, iptal edilmiş (VOID) bir işlemi başarılı saymaya
        // açık bırakırdı — sağlayıcının kendi örnek yanıtı tam olarak öyle.
        if (!Flag(root, "isSuccess") || !AhlPayMessages.IsCaptured(status))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                AhlPayMessages.UnifiedError(form.GetValueOrDefault("responseCode")),
                status ?? Text(root, "errorCode"), Text(root, "message"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: form.GetValueOrDefault("authCode"),
            ConnectorTxnId: form.GetValueOrDefault("transId") ?? form.GetValueOrDefault("hostReferenceNumber"),
            MaskedPan: form.GetValueOrDefault("cardNumber"),
            CardBank: null,
            UnifiedErrors.None, status, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, "api/Payment/Void", reference.OrderId, null, reference.ConnectorTxnId, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, "api/Payment/Refund", request.OrderId,
            AhlPayMessages.Amount(request.AmountMinor), request.ConnectorTxnId, ct);


    private async Task<ConnectorOperationResult> OperationAsync(
        ConnectorCredentials credentials, string path, string orderId, string? amount,
        string? txnId, CancellationToken ct)
    {
        var random = AhlPayMessages.RandomNonce();

        using var response = await SendAsync(credentials, path, new
        {
            merchantId = ParseInt(credentials.Require("merchant_id")),
            memberId = ParseInt(credentials.Require("member_id")),
            orderId,
            totalAmount = amount,
            rnd = random,
            hash = AhlPayMessages.RequestHash(random),
        }, ct);

        return Flag(response.RootElement, "isSuccess")
            ? ConnectorOperationResult.Ok(txnId)
            : ConnectorOperationResult.Fail(
                AhlPayMessages.UnifiedError(Text(response.RootElement, "errorCode")),
                Text(response.RootElement, "errorCode"), Text(response.RootElement, "message"));
    }

    private async Task<string> GetTokenAsync(ConnectorCredentials credentials, CancellationToken ct)
    {
        using var response = await SendAsync(credentials, "api/Security/AuthenticationMerchant", new
        {
            email = credentials.Require("user_code"),
            password = credentials.Require("password"),
        }, ct, withoutToken: true);

        var root = response.RootElement;
        var data = root.TryGetProperty("data", out var d) ? d : default;
        var token = Text(data, "token") ?? Text(root, "token");

        return string.IsNullOrWhiteSpace(token)
            ? throw new ConnectorUnavailableException("AHL Pay belirteci alınamadı.")
            : token;
    }

    private async Task<JsonDocument> SendAsync(
        ConnectorCredentials credentials, string path, object body, CancellationToken ct,
        bool withoutToken = false)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{credentials.Require("gateway_base").TrimEnd('/')}/{path}")
        {
            Content = JsonContent.Create(body),
        };

        if (!withoutToken)
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetTokenAsync(credentials, ct));

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"AHL Pay {path} → {(int)response.StatusCode}.");

            return JsonDocument.Parse(text);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"AHL Pay {path} ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("AHL Pay yanıtı JSON değil.", ex);
        }
    }

    /// <summary>merchantId/memberId sayı gider — dize gönderirsek sağlayıcı 400 döner.</summary>
    private static int ParseInt(string value)
        => int.TryParse(value, out var number)
            ? number
            : throw new ConnectorConfigurationException($"Sayısal olmayan kimlik alanı: '{value}'.");

    private static bool Flag(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;

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
