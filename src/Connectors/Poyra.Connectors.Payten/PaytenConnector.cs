using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Payten;

/// <summary>
/// <b>Payten (MSU)</b> altyapısı — Paratika, VakıfPayS, ZiraatPay ve Payten-MSU aynı
/// platformu kullanır (<c>/api/v2</c>). Sağlayıcı seçimi <c>gateway_base</c> ile yapılır.
///
/// <b>PCI kapsamı:</b> kart İŞYERİ tarafında toplanır. Kart verisi tarayıcıya BASILMAZ:
/// 3D adımı sunucudan sunucuya çağrılır, dönen HTML'deki form tarayıcıya verilir.
///
/// <b>Dönüş doğrulaması iki katmanlıdır:</b>
/// 1. Callback imzası (<c>sdSha512</c>) ön filtredir — tutmayan dönüş hiç sorgulanmaz.
/// 2. Tahsilatın kendisi <c>QUERYTRANSACTION</c> ile SUNUCUDAN okunur; tarayıcının
///    söylediği <c>responseCode</c> hiçbir zaman tek başına kanıt sayılmaz.
///
/// <b>⚠ SERTİFİKASYON DURUMU:</b> alan adları ve imza kodlaması canlı hesapla
/// doğrulanmadan üretime alınmamalı.
/// </summary>
public sealed class PaytenConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "payten";
    public const string HttpClientName = "poyra-payten";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Payten / MSU (Paratika, VakıfPayS, ZiraatPay) — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "Servis adresi (ör. https://entegrasyon.paratika.com.tr/paratika/api/v2)"),
            new CredentialField("merchant", "Üye işyeri no (MERCHANT)"),
            new CredentialField("merchant_user", "API kullanıcısı (MERCHANTUSER)"),
            new CredentialField("merchant_password", "API şifresi (MERCHANTPASSWORD)", Secret: true),
            new CredentialField("secret_key", "Callback imza anahtarı (secretKey)", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Kart İŞYERİ formunda toplanır → PCI kapsamı; banka-hosted giriş yoktur. "
               + "Tahsilat QUERYTRANSACTION ile sunucudan doğrulanır, callback imzası "
               + "(sdSha512) ön filtredir. TODO(cert).");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => throw new ConnectorConfigurationException(
            "Payten banka-hosted kart girişini desteklemiyor; 3DS'li direct akış kullanın.");

    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var amount = PaytenMessages.Amount(request.AmountMinor);

        // 1) Oturum belirteci: tutar ve dönüş adresi SUNUCUDA kayda geçer, sonraki adımda
        //    müşteri bunları değiştiremez.
        var session = await QueryAsync(credentials, new Dictionary<string, string>
        {
            ["ACTION"] = "SESSIONTOKEN",
            ["SESSIONTYPE"] = "PAYMENTSESSION",
            ["MERCHANTPAYMENTID"] = request.OrderId,
            ["CUSTOMER"] = request.OrderId,
            ["CUSTOMERNAME"] = request.Card.HolderName ?? "Poyra Musteri",
            ["CUSTOMEREMAIL"] = "musteri@poyra.local",
            ["CUSTOMERIP"] = request.CustomerIp ?? "0.0.0.0",
            ["RETURNURL"] = callbackUrl,
            ["AMOUNT"] = amount,
            ["CURRENCY"] = request.Currency.ToUpperInvariant(),
            // Elle kurulmuş JSON değil: açıklamada bir tırnak ya da ters eğik çizgi
            // olsaydı gövde bozulur ve istek anlaşılmaz bir hatayla reddedilirdi.
            ["ORDERITEMS"] = JsonSerializer.Serialize(new[]
            {
                new
                {
                    code = "POSCEK",
                    name = request.Description ?? "Siparis",
                    description = string.Empty,
                    quantity = 1,
                    amount = amount,
                },
            }),
        }, ct);

        var token = Text(session.RootElement, "sessionToken");
        if (Text(session.RootElement, "responseCode") != "00" || string.IsNullOrWhiteSpace(token))
            throw new ConnectorUnavailableException(
                $"Payten oturum belirteci alınamadı: {Text(session.RootElement, "responseCode")} "
                + Text(session.RootElement, "errorMsg"));

        // 2) Kart verisi SUNUCUDAN sunucuya gider — tarayıcıya bastığımız HTML'de PAN olmaz.
        var baseUrl = credentials.Require("gateway_base").TrimEnd('/');
        var html = await SendAsync($"{baseUrl}/post/sale3d/{token}", new Dictionary<string, string>
        {
            ["panname"] = request.Card.HolderName ?? "POYRA MUSTERI",
            ["cardOwner"] = request.Card.HolderName ?? "POYRA MUSTERI",
            ["pan"] = request.Card.Pan,
            ["expiryMonth"] = request.Card.ExpiryMonth.ToString("D2"),
            ["expiryYear"] = request.Card.ExpiryYear.ToString("D4"),
            ["cvv"] = request.Card.Cvv ?? string.Empty,
            ["installmentCount"] = Math.Max(1, request.Installments).ToString(),
        }, ct);

        var form = ConnectorHtml.ExtractForm(html);
        if (form is not { } extracted)
            throw new ConnectorUnavailableException("Payten 3D adımında beklenen form dönmedi.");

        return new HostedPaymentForm(extracted.ActionUrl, extracted.Fields);
    }

    /// <summary>
    /// İmza tutmuyorsa dönüş sahtedir; tutuyorsa bile tahsilat henüz kanıtlanmış değildir —
    /// sonuç <see cref="CompleteHostedCallbackAsync"/> içindeki sunucu sorgusundan okunur.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var orderId = form.GetValueOrDefault("merchantPaymentId", string.Empty);

        if (!IsSignatureValid(form, credentials, orderId))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, form.GetValueOrDefault("responseCode"),
                "Payten callback imzası doğrulanamadı.");

        return new HostedCallbackResult(
            false, orderId, null, null, null, null,
            UnifiedErrors.ProcessingError, form.GetValueOrDefault("responseCode"),
            "Dönüş QUERYTRANSACTION ile kesinleştirilmelidir.");
    }

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var orderId = form.GetValueOrDefault("merchantPaymentId", string.Empty);
        var mdStatus = form.GetValueOrDefault("mdStatus");

        if (!IsSignatureValid(form, credentials, orderId))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, form.GetValueOrDefault("responseCode"),
                "Payten callback imzası doğrulanamadı.");

        // Otorite BURASI: tarayıcının ne dediğinden bağımsız olarak sunucuya sorulur.
        using var query = await QueryAsync(credentials, new Dictionary<string, string>
        {
            ["ACTION"] = "QUERYTRANSACTION",
            ["MERCHANTPAYMENTID"] = orderId,
        }, ct);

        var operation = FirstTransaction(query.RootElement);
        var code = Text(operation, "responseCode") ?? Text(query.RootElement, "responseCode");

        if (code != "00")
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                PaytenMessages.UnifiedError(code, mdStatus), code,
                Text(operation, "responseMsg") ?? Text(query.RootElement, "errorMsg"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Text(operation, "pgTranApprCode"),
            ConnectorTxnId: Text(operation, "pgTranId") ?? form.GetValueOrDefault("pgTranId"),
            MaskedPan: Text(operation, "cardNumberMasked"),
            CardBank: Text(operation, "paymentSystem"),
            UnifiedErrors.None, code, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, new Dictionary<string, string>
        {
            ["ACTION"] = "VOID",
            ["PGTRANID"] = reference.ConnectorTxnId ?? string.Empty,
            ["REFLECTCOMMISSION"] = "No",
        }, reference.ConnectorTxnId, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, new Dictionary<string, string>
        {
            ["ACTION"] = "REFUND",
            ["PGTRANID"] = request.ConnectorTxnId ?? string.Empty,
            ["AMOUNT"] = PaytenMessages.Amount(request.AmountMinor),
            ["CURRENCY"] = request.Currency.ToUpperInvariant(),
            ["REFLECTCOMMISSION"] = "No",
        }, request.ConnectorTxnId, ct);


    private static bool IsSignatureValid(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, string orderId)
        => PaytenMessages.IsSignatureValid(
            form.GetValueOrDefault("sdSha512") ?? form.GetValueOrDefault("SD_SHA512"),
            orderId,
            form.GetValueOrDefault("customerId"),
            form.GetValueOrDefault("sessionToken"),
            form.GetValueOrDefault("responseCode"),
            form.GetValueOrDefault("random"),
            credentials.Require("secret_key"));

    private async Task<ConnectorOperationResult> OperationAsync(
        ConnectorCredentials credentials, Dictionary<string, string> fields, string? txnId,
        CancellationToken ct)
    {
        using var response = await QueryAsync(credentials, fields, ct);
        var code = Text(response.RootElement, "responseCode");

        return code == "00"
            ? ConnectorOperationResult.Ok(txnId)
            : ConnectorOperationResult.Fail(
                PaytenMessages.UnifiedError(code, null), code,
                Text(response.RootElement, "errorMsg") ?? Text(response.RootElement, "responseMsg"));
    }

    private async Task<JsonDocument> QueryAsync(
        ConnectorCredentials credentials, Dictionary<string, string> fields, CancellationToken ct)
    {
        var body = new Dictionary<string, string>(fields, StringComparer.Ordinal)
        {
            ["MERCHANT"] = credentials.Require("merchant"),
            ["MERCHANTUSER"] = credentials.Require("merchant_user"),
            ["MERCHANTPASSWORD"] = credentials.Require("merchant_password"),
        };

        var text = await SendAsync(credentials.Require("gateway_base").TrimEnd('/'), body, ct);

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("Payten yanıtı JSON değil.", ex);
        }
    }

    private async Task<string> SendAsync(
        string url, Dictionary<string, string> fields, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(url, new FormUrlEncodedContent(fields), ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"Payten {(int)response.StatusCode} döndü.");

            return text;
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException("Payten ucuna ulaşılamadı.", ex);
        }
    }

    private static JsonElement FirstTransaction(JsonElement root)
    {
        if (root.TryGetProperty("transactionList", out var list)
            && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
            return list[0];

        if (root.TryGetProperty("transactions", out var other)
            && other.ValueKind == JsonValueKind.Array && other.GetArrayLength() > 0)
            return other[0];

        return root;
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
