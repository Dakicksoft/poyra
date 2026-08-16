using System.Text;
using System.Xml.Linq;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Posnet;

/// <summary>
/// Posnet — Yapı Kredi sanal POS.
///
/// Posnet'te "banka sayfasında kart girme" (3d_pay_hosting) modeli YOKTUR. 3DS akışı
/// OOS'tur (Out Of Store): kart İŞYERİNİN formunda toplanır, sunucudan sunucuya
/// `oosRequestData` çağrılır, banka `data1/data2/sign` döner, müşteri bu üçüyle
/// bankanın 3DS sayfasına POST edilir. Dönüşte `oosResolveMerchantData` ile MAC
/// doğrulanır ve `oosTranData` ile işlem finansallaştırılır.
///
/// SONUÇ (önemli): bu konnektör PCI KAPSAMINDADIR. InitiateHostedPaymentAsync
/// bilinçli olarak desteklenmez — rota, hosted akışta bu hesabı aday olarak atlar.
/// Üretimde yalnız PCI DSS hizmet sağlayıcı sertifikasyonu tamamlandıktan sonra
/// açılmalıdır.
///
/// TODO(cert): uç adları, MAC alan sıraları ve XML şeması YKB sertifikasyon testinde
/// birebir doğrulanacaktır.
/// </summary>
public sealed class PosnetConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "posnet";
    public const string HttpClientName = "poyra-posnet";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Posnet Sanal POS (Yapı Kredi)",
        ConnectorType.BankVirtualPos,
        [
            new CredentialField("gateway_base", "Gateway adresi (ör. https://setmpos.ykb.com)"),
            new CredentialField("merchant_id", "Üye işyeri no (MerchantID)"),
            new CredentialField("terminal_id", "Terminal No (TerminalID)"),
            new CredentialField("pos_net_id", "PosnetID"),
            new CredentialField("enc_key", "MAC anahtarı (encKey)", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "OOS 3DS: kart İŞYERİ formunda toplanır → PCI kapsamı. "
               + "Banka-hosted kart girişi Posnet'te yoktur; hosted akışta bu hesap atlanır. "
               + "PCI DSS sertifikasyonu tamamlanmadan üretimde açmayın.");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => throw new ConnectorConfigurationException(
            "Posnet banka-hosted kart girişini desteklemiyor; 3DS'li direct akış (OOS) kullanın.");

    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials, CancellationToken ct)
    {
        var gatewayBase = credentials.Require("gateway_base").TrimEnd('/');
        var merchantId = credentials.Require("merchant_id");
        var terminalId = credentials.Require("terminal_id");
        var orderId = PosnetMac.OrderId(request.OrderId);
        var amount = PosnetMac.Amount(request.AmountMinor);
        var currency = PosnetCurrency.Code(request.Currency);

        var oos = new XElement("posnetRequest",
            new XElement("mid", merchantId),
            new XElement("tid", terminalId),
            new XElement("oosRequestData",
                new XElement("posnetid", credentials.Require("pos_net_id")),
                new XElement("XID", orderId),
                new XElement("amount", amount),
                new XElement("currencyCode", currency),
                new XElement("installment", Installment(request.Installments)),
                new XElement("tranType", "Sale"),
                new XElement("cardHolderName", request.Card.HolderName ?? ""),
                new XElement("ccno", request.Card.Pan),
                new XElement("expDate", ExpiryYymm(request.Card)),
                new XElement("cvc", request.Card.Cvv ?? "")));

        var response = await PostAsync(gatewayBase, oos, ct);
        var data = response.Element("oosRequestDataResponse");

        if (data is null || response.Element("approved")?.Value != "1")
        {
            var code = response.Element("respCode")?.Value;
            throw new ConnectorConfigurationException(
                $"Posnet oosRequestData reddetti: {code} {response.Element("respText")?.Value}");
        }

        // Müşteri bu üç alanla bankanın 3DS sayfasına POST edilir; kart verisi ARTIK GİTMEZ
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mid"] = merchantId,
            ["posnetID"] = credentials.Require("pos_net_id"),
            ["posnetData"] = data.Element("data1")?.Value ?? "",
            ["posnetData2"] = data.Element("data2")?.Value ?? "",
            ["digest"] = data.Element("sign")?.Value ?? "",
            ["merchantReturnURL"] = callbackUrl,
            ["url"] = "",
            ["lang"] = "tr",
        };

        return new HostedPaymentForm($"{gatewayBase}/3DSWebService/YKBPaymentService", fields);
    }

    /// <summary>
    /// Posnet dönüşü tek başına yeterli DEĞİLDİR: banka yalnız 3DS sonucunu ve imzalı
    /// veriyi yollar; işlem <see cref="CompleteHostedCallbackAsync"/> içindeki ikinci
    /// sunucu çağrısıyla finansallaşır. Bu yüzden burada yalnız biçimsel kontrol yapılır.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var orderId = Get(form, "xid") ?? Get(form, "XID") ?? "";
        var mdStatus = Get(form, "mdStatus") ?? Get(form, "mdstatus");

        if (mdStatus is not "1")
            return new HostedCallbackResult(false, orderId, null, null, null, "Yapı Kredi",
                UnifiedErrors.ThreeDsFailed, mdStatus, Get(form, "mdErrorMessage"));

        return new HostedCallbackResult(false, orderId, null, null, null, "Yapı Kredi",
            UnifiedErrors.ProcessingError, null,
            "Posnet dönüşü sunucu doğrulaması gerektirir (CompleteHostedCallbackAsync).");
    }

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var gatewayBase = credentials.Require("gateway_base").TrimEnd('/');
        var merchantId = credentials.Require("merchant_id");
        var terminalId = credentials.Require("terminal_id");

        var bankData = Get(form, "BankPacket") ?? Get(form, "bankPacket") ?? "";
        var merchantData = Get(form, "MerchantPacket") ?? Get(form, "merchantPacket") ?? "";
        var sign = Get(form, "Sign") ?? Get(form, "sign") ?? "";
        var mdStatus = Get(form, "mdStatus") ?? Get(form, "mdstatus") ?? "";
        var orderId = Get(form, "xid") ?? Get(form, "XID") ?? "";

        // 1- 3DS başarısızsa finansal çağrı YAPILMAZ — boşuna banka yükü ve yanlış iz olurdu
        if (mdStatus is not "1")
            return new HostedCallbackResult(false, orderId, null, null, null, "Yapı Kredi",
                UnifiedErrors.ThreeDsFailed, mdStatus, Get(form, "mdErrorMessage"));

        // 2- Bankanın gönderdiği paketi bankaya çözdür (MAC doğrulaması banka tarafında)
        var resolve = new XElement("posnetRequest",
            new XElement("mid", merchantId),
            new XElement("tid", terminalId),
            new XElement("oosResolveMerchantData",
                new XElement("bankData", bankData),
                new XElement("merchantData", merchantData),
                new XElement("sign", sign),
                new XElement("mac", PosnetMac.Mac(
                    orderId,
                    Get(form, "amount") ?? "0",
                    Get(form, "currency") ?? "TL",
                    merchantId,
                    PosnetMac.FirstHash(credentials.Require("enc_key"), terminalId)))));

        var resolved = await PostAsync(gatewayBase, resolve, ct);
        if (resolved.Element("approved")?.Value != "1")
            return new HostedCallbackResult(false, orderId, null, null, null, "Yapı Kredi",
                UnifiedErrors.SignatureInvalid, resolved.Element("respCode")?.Value,
                resolved.Element("respText")?.Value ?? "Posnet MAC doğrulaması başarısız.");

        // 3- Finansallaştır
        var finalize = new XElement("posnetRequest",
            new XElement("mid", merchantId),
            new XElement("tid", terminalId),
            new XElement("oosTranData",
                new XElement("bankData", bankData),
                new XElement("wpAmount", "0"),
                new XElement("mac", PosnetMac.Mac(
                    orderId,
                    Get(form, "amount") ?? "0",
                    Get(form, "currency") ?? "TL",
                    merchantId,
                    PosnetMac.FirstHash(credentials.Require("enc_key"), terminalId)))));

        var result = await PostAsync(gatewayBase, finalize, ct);
        var approved = result.Element("approved")?.Value == "1";
        var respCode = result.Element("respCode")?.Value;

        return approved
            ? new HostedCallbackResult(true, orderId,
                AuthCode: result.Element("authCode")?.Value,
                ConnectorTxnId: result.Element("hostlogkey")?.Value,
                MaskedPan: resolved.Element("oosResolveMerchantDataResponse")?.Element("maskedPan")?.Value,
                CardBank: "Yapı Kredi",
                UnifiedCode: "",
                RawCode: respCode,
                RawMessage: null)
            : new HostedCallbackResult(false, orderId, null, null, null, "Yapı Kredi",
                PosnetErrorMap.ToUnified(respCode), respCode, result.Element("respText")?.Value);
    }

    public async Task<DirectAuthorizeResult?> AuthorizeDirectAsync(
        DirectPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var gatewayBase = credentials.Require("gateway_base").TrimEnd('/');

        var sale = new XElement("posnetRequest",
            new XElement("mid", credentials.Require("merchant_id")),
            new XElement("tid", credentials.Require("terminal_id")),
            new XElement("tranDateRequired", "1"),
            new XElement("sale",
                new XElement("orderID", PosnetMac.OrderId(request.OrderId)),
                new XElement("installment", Installment(request.Installments)),
                new XElement("amount", PosnetMac.Amount(request.AmountMinor)),
                new XElement("currencyCode", PosnetCurrency.Code(request.Currency)),
                new XElement("ccno", request.Card.Pan),
                new XElement("expDate", ExpiryYymm(request.Card)),
                new XElement("cvc", request.Card.Cvv ?? "")));

        var response = await PostAsync(gatewayBase, sale, ct);
        var approved = response.Element("approved")?.Value == "1";
        var respCode = response.Element("respCode")?.Value;

        return approved
            ? new DirectAuthorizeResult(true,
                response.Element("authCode")?.Value,
                response.Element("hostlogkey")?.Value,
                CardNumbers.Mask(request.Card.Pan), "", respCode, null)
            : new DirectAuthorizeResult(false, null, null, CardNumbers.Mask(request.Card.Pan),
                PosnetErrorMap.ToUnified(respCode), respCode, response.Element("respText")?.Value);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => SendOperationAsync(credentials, new XElement("reverse",
            new XElement("transaction", "sale"),
            new XElement("hostLogKey", reference.ConnectorTxnId ?? ""),
            new XElement("authCode", "")), ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => SendOperationAsync(credentials, new XElement("return",
            new XElement("amount", PosnetMac.Amount(request.AmountMinor)),
            new XElement("currencyCode", PosnetCurrency.Code(request.Currency)),
            new XElement("hostLogKey", request.ConnectorTxnId ?? ""),
            new XElement("orderID", PosnetMac.OrderId(request.OrderId))), ct);

    public async Task<ConnectorProbeResult?> ProbeAsync(ConnectorCredentials credentials, CancellationToken ct)
    {
        // Posnet'te hafif "ping" ucu yok; erişilebilirlik gateway'e TCP/HTTP dokunuşuyla ölçülür
        try
        {
            var response = await httpClientFactory.CreateClient(HttpClientName)
                .GetAsync(credentials.Require("gateway_base").TrimEnd('/'), ct);
            return new ConnectorProbeResult(true, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new ConnectorProbeResult(false, ex.Message);
        }
    }

    private async Task<ConnectorOperationResult> SendOperationAsync(
        ConnectorCredentials credentials, XElement operation, CancellationToken ct)
    {
        var envelope = new XElement("posnetRequest",
            new XElement("mid", credentials.Require("merchant_id")),
            new XElement("tid", credentials.Require("terminal_id")),
            operation);

        var response = await PostAsync(credentials.Require("gateway_base").TrimEnd('/'), envelope, ct);
        var respCode = response.Element("respCode")?.Value;

        return response.Element("approved")?.Value == "1"
            ? ConnectorOperationResult.Ok(response.Element("hostlogkey")?.Value)
            : ConnectorOperationResult.Fail(
                PosnetErrorMap.ToUnified(respCode), respCode, response.Element("respText")?.Value);
    }

    private async Task<XElement> PostAsync(string gatewayBase, XElement request, CancellationToken ct)
    {
        using var content = new StringContent(
            "xmldata=" + Uri.EscapeDataString(request.ToString(SaveOptions.DisableFormatting)),
            Encoding.UTF8, "application/x-www-form-urlencoded");

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName)
                .PostAsync($"{gatewayBase}/PosnetWebService/XML", content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorUnavailableException($"Posnet'e ulaşılamadı: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
            throw new ConnectorUnavailableException($"Posnet HTTP {(int)response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return XElement.Parse(body);
        }
        catch (Exception ex)
        {
            throw new ConnectorUnavailableException($"Posnet yanıtı ayrıştırılamadı: {ex.Message}", ex);
        }
    }

    /// <summary>Posnet taksitsiz işlemde "00" bekler, boş string değil.</summary>
    private static string Installment(int installments)
        => installments > 1 ? installments.ToString("00") : "00";

    /// <summary>Posnet son kullanma tarihini YYAA (yıl-ay) ister — çoğu bankanın tersi.</summary>
    private static string ExpiryYymm(CardData card)
        => $"{card.ExpiryYear % 100:00}{card.ExpiryMonth:00}";

    private static string? Get(IReadOnlyDictionary<string, string> form, string key)
        => form.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
}

public static class PosnetCurrency
{
    /// <summary>Posnet ISO sayısal kod DEĞİL, harf kodu kullanır: TL/US/EU.</summary>
    public static string Code(string currency) => currency.ToUpperInvariant() switch
    {
        "TRY" => "TL",
        "USD" => "US",
        "EUR" => "EU",
        _ => "TL",
    };
}

public static class PosnetErrorMap
{
    /// <summary>Posnet respCode'ları banka özeldir; birleşik hata sözlüğüne çevrilir.</summary>
    public static string ToUnified(string? code) => code switch
    {
        "0" or "00" => "",
        "0148" or "0051" => UnifiedErrors.InsufficientFunds,
        "0054" => UnifiedErrors.ExpiredCard,
        "0057" or "0062" => UnifiedErrors.NotPermitted,
        "0061" or "0065" => UnifiedErrors.LimitExceeded,
        "0041" or "0043" => UnifiedErrors.CardDeclined, // kayıp/çalıntı — müşteriye ayrıntı verilmez
        "0014" => UnifiedErrors.InvalidCard,
        "0091" => UnifiedErrors.IssuerUnavailable,
        null or "" => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined,
    };
}
