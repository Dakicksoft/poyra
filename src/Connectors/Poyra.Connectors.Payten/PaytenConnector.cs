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
        var tutar = PaytenMessages.Amount(request.AmountMinor);

        // 1) Oturum belirteci: tutar ve dönüş adresi SUNUCUDA kayda geçer, sonraki adımda
        //    müşteri bunları değiştiremez.
        var oturum = await SorAsync(credentials, new Dictionary<string, string>
        {
            ["ACTION"] = "SESSIONTOKEN",
            ["SESSIONTYPE"] = "PAYMENTSESSION",
            ["MERCHANTPAYMENTID"] = request.OrderId,
            ["CUSTOMER"] = request.OrderId,
            ["CUSTOMERNAME"] = request.Card.HolderName ?? "Poyra Musteri",
            ["CUSTOMEREMAIL"] = "musteri@poyra.local",
            ["CUSTOMERIP"] = request.CustomerIp ?? "0.0.0.0",
            ["RETURNURL"] = callbackUrl,
            ["AMOUNT"] = tutar,
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
                    amount = tutar,
                },
            }),
        }, ct);

        var belirtec = Metin(oturum.RootElement, "sessionToken");
        if (Metin(oturum.RootElement, "responseCode") != "00" || string.IsNullOrWhiteSpace(belirtec))
            throw new ConnectorUnavailableException(
                $"Payten oturum belirteci alınamadı: {Metin(oturum.RootElement, "responseCode")} "
                + Metin(oturum.RootElement, "errorMsg"));

        // 2) Kart verisi SUNUCUDAN sunucuya gider — tarayıcıya bastığımız HTML'de PAN olmaz.
        var taban = credentials.Require("gateway_base").TrimEnd('/');
        var html = await GonderAsync($"{taban}/post/sale3d/{belirtec}", new Dictionary<string, string>
        {
            ["panname"] = request.Card.HolderName ?? "POYRA MUSTERI",
            ["cardOwner"] = request.Card.HolderName ?? "POYRA MUSTERI",
            ["pan"] = request.Card.Pan,
            ["expiryMonth"] = request.Card.ExpiryMonth.ToString("D2"),
            ["expiryYear"] = request.Card.ExpiryYear.ToString("D4"),
            ["cvv"] = request.Card.Cvv ?? string.Empty,
            ["installmentCount"] = Math.Max(1, request.Installments).ToString(),
        }, ct);

        var form = ConnectorHtml.FormuCikar(html);
        if (form is not { } cikan)
            throw new ConnectorUnavailableException("Payten 3D adımında beklenen form dönmedi.");

        return new HostedPaymentForm(cikan.ActionUrl, cikan.Fields);
    }

    /// <summary>
    /// İmza tutmuyorsa dönüş sahtedir; tutuyorsa bile tahsilat henüz kanıtlanmış değildir —
    /// sonuç <see cref="CompleteHostedCallbackAsync"/> içindeki sunucu sorgusundan okunur.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var orderId = form.GetValueOrDefault("merchantPaymentId", string.Empty);

        if (!ImzaGecerli(form, credentials, orderId))
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

        if (!ImzaGecerli(form, credentials, orderId))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, form.GetValueOrDefault("responseCode"),
                "Payten callback imzası doğrulanamadı.");

        // Otorite BURASI: tarayıcının ne dediğinden bağımsız olarak sunucuya sorulur.
        using var sorgu = await SorAsync(credentials, new Dictionary<string, string>
        {
            ["ACTION"] = "QUERYTRANSACTION",
            ["MERCHANTPAYMENTID"] = orderId,
        }, ct);

        var islem = IlkIslem(sorgu.RootElement);
        var kod = Metin(islem, "responseCode") ?? Metin(sorgu.RootElement, "responseCode");

        if (kod != "00")
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                PaytenMessages.UnifiedError(kod, mdStatus), kod,
                Metin(islem, "responseMsg") ?? Metin(sorgu.RootElement, "errorMsg"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Metin(islem, "pgTranApprCode"),
            ConnectorTxnId: Metin(islem, "pgTranId") ?? form.GetValueOrDefault("pgTranId"),
            MaskedPan: Metin(islem, "cardNumberMasked"),
            CardBank: Metin(islem, "paymentSystem"),
            UnifiedErrors.None, kod, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, new Dictionary<string, string>
        {
            ["ACTION"] = "VOID",
            ["PGTRANID"] = reference.ConnectorTxnId ?? string.Empty,
            ["REFLECTCOMMISSION"] = "No",
        }, reference.ConnectorTxnId, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, new Dictionary<string, string>
        {
            ["ACTION"] = "REFUND",
            ["PGTRANID"] = request.ConnectorTxnId ?? string.Empty,
            ["AMOUNT"] = PaytenMessages.Amount(request.AmountMinor),
            ["CURRENCY"] = request.Currency.ToUpperInvariant(),
            ["REFLECTCOMMISSION"] = "No",
        }, request.ConnectorTxnId, ct);


    private static bool ImzaGecerli(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, string orderId)
        => PaytenMessages.ImzaGecerli(
            form.GetValueOrDefault("sdSha512") ?? form.GetValueOrDefault("SD_SHA512"),
            orderId,
            form.GetValueOrDefault("customerId"),
            form.GetValueOrDefault("sessionToken"),
            form.GetValueOrDefault("responseCode"),
            form.GetValueOrDefault("random"),
            credentials.Require("secret_key"));

    private async Task<ConnectorOperationResult> IslemAsync(
        ConnectorCredentials credentials, Dictionary<string, string> alanlar, string? txnId,
        CancellationToken ct)
    {
        using var yanit = await SorAsync(credentials, alanlar, ct);
        var kod = Metin(yanit.RootElement, "responseCode");

        return kod == "00"
            ? ConnectorOperationResult.Ok(txnId)
            : ConnectorOperationResult.Fail(
                PaytenMessages.UnifiedError(kod, null), kod,
                Metin(yanit.RootElement, "errorMsg") ?? Metin(yanit.RootElement, "responseMsg"));
    }

    private async Task<JsonDocument> SorAsync(
        ConnectorCredentials credentials, Dictionary<string, string> alanlar, CancellationToken ct)
    {
        var govde = new Dictionary<string, string>(alanlar, StringComparer.Ordinal)
        {
            ["MERCHANT"] = credentials.Require("merchant"),
            ["MERCHANTUSER"] = credentials.Require("merchant_user"),
            ["MERCHANTPASSWORD"] = credentials.Require("merchant_password"),
        };

        var metin = await GonderAsync(credentials.Require("gateway_base").TrimEnd('/'), govde, ct);

        try
        {
            return JsonDocument.Parse(metin);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("Payten yanıtı JSON değil.", ex);
        }
    }

    private async Task<string> GonderAsync(
        string adres, Dictionary<string, string> alanlar, CancellationToken ct)
    {
        try
        {
            var istemci = httpClientFactory.CreateClient(HttpClientName);
            using var yanit = await istemci.PostAsync(adres, new FormUrlEncodedContent(alanlar), ct);
            var metin = await yanit.Content.ReadAsStringAsync(ct);

            if (!yanit.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"Payten {(int)yanit.StatusCode} döndü.");

            return metin;
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException("Payten ucuna ulaşılamadı.", ex);
        }
    }

    private static JsonElement IlkIslem(JsonElement kok)
    {
        if (kok.TryGetProperty("transactionList", out var liste)
            && liste.ValueKind == JsonValueKind.Array && liste.GetArrayLength() > 0)
            return liste[0];

        if (kok.TryGetProperty("transactions", out var digeri)
            && digeri.ValueKind == JsonValueKind.Array && digeri.GetArrayLength() > 0)
            return digeri[0];

        return kok;
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
