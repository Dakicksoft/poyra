using System.Text;
using System.Xml.Linq;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Gvp;

/// <summary>
/// Garanti Sanal POS (GVP).
/// </summary>
public sealed class GvpConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "gvp";
    public const string HttpClientName = "poyra-gvp";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Garanti Sanal POS (GVP)",
        ConnectorType.BankVirtualPos,
        [
            new CredentialField("gateway_base", "Gateway adresi (ör. https://sanalposprov.garanti.com.tr)"),
            new CredentialField("terminal_id", "Terminal No"),
            new CredentialField("merchant_id", "Üye işyeri no (MerchantID)"),
            new CredentialField("prov_user_id", "Provizyon kullanıcısı (PROVAUT)"),
            new CredentialField("prov_password", "Provizyon şifresi", Secret: true),
            new CredentialField("store_key", "3D Store Key", Secret: true),
            new CredentialField("refund_user_id", "İade kullanıcısı (PROVRFN)", Required: false),
            new CredentialField("refund_password", "İade şifresi", Secret: true, Required: false),
            new CredentialField("mode", "Mod (PROD/TEST)", Required: false),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "3D_PAY modeli: kart verisi bankada girilir. Tutar kuruş cinsinden gider.");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var gatewayBase = credentials.Require("gateway_base").TrimEnd('/');
        var terminalId = credentials.Require("terminal_id");
        var amount = request.AmountMinor.ToString(); // GVP: kuruş!
        var currency = Iso4217.NumericCode(request.Currency);
        var installments = request.Installments > 1 ? request.Installments.ToString() : "";

        var hashedPassword = GvpHash.HashedPassword(credentials.Require("prov_password"), terminalId);
        var hash = GvpHash.ThreeDsRequestHash(
            terminalId, request.OrderId, amount, currency,
            request.CallbackUrl, request.CallbackUrl, "sales", installments,
            credentials.Require("store_key"), hashedPassword);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["secure3dsecuritylevel"] = "3D_PAY",
            ["apiversion"] = "512",
            ["mode"] = credentials.Get("mode") ?? "PROD",
            ["terminalprovuserid"] = credentials.Require("prov_user_id"),
            ["terminaluserid"] = credentials.Require("prov_user_id"),
            ["terminalmerchantid"] = credentials.Require("merchant_id"),
            ["terminalid"] = terminalId,
            ["orderid"] = request.OrderId,
            ["txntype"] = "sales",
            ["txnamount"] = amount,
            ["txncurrencycode"] = currency,
            ["txninstallmentcount"] = installments,
            ["successurl"] = request.CallbackUrl,
            ["errorurl"] = request.CallbackUrl,
            ["customeripaddress"] = request.CustomerIp ?? "127.0.0.1",
            ["lang"] = "tr",
            // TODO(cert): txntimestamp biçimi banka sertifikasyonunda doğrulanacak
            ["txntimestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["refreshtime"] = "0",
            ["secure3dhash"] = hash,
        };

        return Task.FromResult(new HostedPaymentForm($"{gatewayBase}/servlet/gt3dengine", fields));
    }

    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var orderId = Get(form, "orderid") ?? Get(form, "oid") ?? "";

        if (!GvpHash.ValidateCallback(form, credentials.Require("store_key")))
            return new HostedCallbackResult(false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, null, "GVP callback hash doğrulaması başarısız.");

        var mdStatus = Get(form, "mdstatus");
        var procCode = Get(form, "procreturncode");
        var errMsg = Get(form, "errmsg") ?? Get(form, "mderrormessage");

        if (mdStatus is not ("1" or "2" or "3" or "4"))
            return new HostedCallbackResult(false, orderId, null, null, null, null,
                UnifiedErrors.ThreeDsFailed, $"mdstatus={mdStatus}", errMsg);

        if (procCode != "00")
            return new HostedCallbackResult(false, orderId, null, Get(form, "hostrefnum"), null, null,
                GvpErrorMap.ToUnified(procCode), procCode, errMsg);

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Get(form, "authcode"),
            ConnectorTxnId: Get(form, "hostrefnum"),
            MaskedPan: null, // GVP dönüşünde maskeli PAN standard değil
            CardBank: "Garanti BBVA",
            UnifiedCode: "",
            RawCode: procCode,
            RawMessage: null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => SendApiAsync(credentials, reference.OrderId, "void", amountMinor: null,
            currency: "TRY", reference.ConnectorTxnId, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => SendApiAsync(credentials, request.OrderId, "refund", request.AmountMinor,
            request.Currency, request.ConnectorTxnId, ct);

    private async Task<ConnectorOperationResult> SendApiAsync(
        ConnectorCredentials credentials, string orderId, string txnType,
        long? amountMinor, string currency, string? retrefNum, CancellationToken ct)
    {
        var gatewayBase = credentials.Require("gateway_base").TrimEnd('/');
        var terminalId = credentials.Require("terminal_id");
        // İade/iptal ayrı kullanıcı ister (PROVRFN); tanımsızsa provizyon kullanıcısına düşer
        var userId = credentials.Get("refund_user_id") ?? credentials.Require("prov_user_id");
        var password = credentials.Get("refund_password") ?? credentials.Require("prov_password");

        var amount = (amountMinor ?? 0).ToString();
        var currencyCode = Iso4217.NumericCode(currency);
        var hashedPassword = GvpHash.HashedPassword(password, terminalId);
        // TODO(cert): XML API hash alan sırası banka sertifikasyonunda doğrulanacak
        var hashData = GvpHash.ApiRequestHash(orderId, terminalId, amount, currencyCode, hashedPassword);

        var transaction = new XElement("Transaction",
            new XElement("Type", txnType),
            new XElement("InstallmentCnt", ""),
            new XElement("Amount", amount),
            new XElement("CurrencyCode", currencyCode),
            new XElement("CardholderPresentCode", "0"),
            new XElement("MotoInd", "N"));
        if (retrefNum is not null)
            transaction.Add(new XElement("OriginalRetrefNum", retrefNum));

        var envelope = new XElement("GVPSRequest",
            new XElement("Mode", credentials.Get("mode") ?? "PROD"),
            new XElement("Version", "512"),
            new XElement("Terminal",
                new XElement("ProvUserID", userId),
                new XElement("HashData", hashData),
                new XElement("UserID", userId),
                new XElement("ID", terminalId),
                new XElement("MerchantID", credentials.Require("merchant_id"))),
            new XElement("Customer", new XElement("IPAddress", "127.0.0.1")),
            new XElement("Order", new XElement("OrderID", orderId)),
            transaction);

        using var content = new StringContent(
            "data=" + Uri.EscapeDataString(envelope.ToString(SaveOptions.DisableFormatting)),
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName)
                .PostAsync($"{gatewayBase}/VPServlet", content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorUnavailableException($"GVP API'ye ulaşılamadı: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
            throw new ConnectorUnavailableException($"GVP API HTTP {(int)response.StatusCode}");

        return ParseApiResponse(await response.Content.ReadAsStringAsync(ct));
    }

    private static ConnectorOperationResult ParseApiResponse(string xml)
    {
        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (Exception)
        {
            return ConnectorOperationResult.Fail(UnifiedErrors.ProcessingError, null, "Geçersiz GVP API yanıtı");
        }

        var responseElement = root.Element("Transaction")?.Element("Response");
        var code = responseElement?.Element("Code")?.Value;
        var reasonCode = responseElement?.Element("ReasonCode")?.Value;
        var message = responseElement?.Element("ErrorMsg")?.Value
                      ?? responseElement?.Element("Message")?.Value;
        var retrefNum = root.Element("Transaction")?.Element("RetrefNum")?.Value;

        return code == "00"
            ? ConnectorOperationResult.Ok(retrefNum)
            : ConnectorOperationResult.Fail(GvpErrorMap.ToUnified(reasonCode ?? code), reasonCode ?? code, message);
    }

    private static string? Get(IReadOnlyDictionary<string, string> form, string key)
        => form.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
}

public static class GvpErrorMap
{
    public static string ToUnified(string? code) => code switch
    {
        "00" => "",
        "05" or "01" or "02" or "34" => UnifiedErrors.CardDeclined,
        "51" => UnifiedErrors.InsufficientFunds,
        "33" or "54" => UnifiedErrors.ExpiredCard,
        "14" or "15" or "56" => UnifiedErrors.InvalidCard,
        "13" => UnifiedErrors.InvalidAmount,
        "57" or "58" or "62" => UnifiedErrors.NotPermitted,
        "61" or "65" => UnifiedErrors.LimitExceeded,
        "91" or "96" => UnifiedErrors.IssuerUnavailable,
        _ => UnifiedErrors.ProcessingError,
    };
}
