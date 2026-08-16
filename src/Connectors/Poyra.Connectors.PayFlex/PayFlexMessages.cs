using System.Xml.Linq;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.PayFlex;

public sealed record PayFlexRegisterResult(
    string ResultCode, string? ResultDetail, string? PaymentToken, string? CommonPaymentUrl);

public sealed record PayFlexPaymentResult(
    string ResultCode, string? ResultDetail, string? OrderId,
    string? TransactionId, string? AuthCode, string? MaskedPan);


public static class PayFlexMessages
{
    public static string BuildRegisterPaymentXml(
        ConnectorCredentials credentials, HostedPaymentRequest request)
        => new XElement("VposRequest",
            new XElement("MerchantId", credentials.Require("merchant_id")),
            new XElement("Password", credentials.Require("password")),
            new XElement("TerminalNo", credentials.Require("terminal_no")),
            new XElement("TransactionType", "Sale"),
            new XElement("OrderId", request.OrderId),
            new XElement("CurrencyAmount", Iso4217.FormatAmount(request.AmountMinor)), // "1499.00"
            new XElement("CurrencyCode", Iso4217.NumericCode(request.Currency)),
            new XElement("NumberOfInstallments",
                request.Installments > 1 ? request.Installments.ToString() : ""),
            new XElement("PaymentType", "CommonPayment"), // Ortak Ödeme Sayfası — kart bankada girilir
            new XElement("SuccessUrl", request.CallbackUrl),
            new XElement("FailUrl", request.CallbackUrl),
            new XElement("RequestLanguage", "TR-TR"),
            new XElement("ClientIp", request.CustomerIp ?? "127.0.0.1"))
            .ToString(SaveOptions.DisableFormatting);

    public static PayFlexRegisterResult ParseRegisterResponse(string xml)
    {
        var root = XElement.Parse(xml);
        return new PayFlexRegisterResult(
            Value(root, "ResultCode") ?? "9999",
            Value(root, "ResultDetail"),
            Value(root, "PaymentToken"),
            Value(root, "CommonPaymentUrl") ?? Value(root, "CommonPaymentPageUrl"));
    }

    public static string BuildInquiryXml(ConnectorCredentials credentials, string paymentToken)
        => new XElement("VposRequest",
            new XElement("MerchantId", credentials.Require("merchant_id")),
            new XElement("Password", credentials.Require("password")),
            new XElement("TerminalNo", credentials.Require("terminal_no")),
            new XElement("TransactionType", "GetPaymentResult"), // TODO(cert): uç/alan adı doğrulanacak
            new XElement("PaymentToken", paymentToken))
            .ToString(SaveOptions.DisableFormatting);

    public static PayFlexPaymentResult ParsePaymentResult(string xml)
    {
        var root = XElement.Parse(xml);
        return new PayFlexPaymentResult(
            Value(root, "ResultCode") ?? "9999",
            Value(root, "ResultDetail"),
            Value(root, "OrderId"),
            Value(root, "TransactionId"),
            Value(root, "AuthCode"),
            Value(root, "MaskedPan") ?? Value(root, "Pan"));
    }

    public static string BuildOperationXml(
        ConnectorCredentials credentials, string transactionType,
        string referenceTransactionId, long? amountMinor, string currency, string clientIp = "127.0.0.1")
    {
        var root = new XElement("VposRequest",
            new XElement("MerchantId", credentials.Require("merchant_id")),
            new XElement("Password", credentials.Require("password")),
            new XElement("TerminalNo", credentials.Require("terminal_no")),
            new XElement("TransactionType", transactionType), // Cancel | Refund
            new XElement("ReferenceTransactionId", referenceTransactionId),
            new XElement("ClientIp", clientIp));

        if (amountMinor is { } amount) // Refund kısmi tutar taşır; Cancel taşımaz
            root.Add(
                new XElement("CurrencyAmount", Iso4217.FormatAmount(amount)),
                new XElement("CurrencyCode", Iso4217.NumericCode(currency)));

        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>PayFlex ResultCode → birleşik sözlük. "0000" onaydır.</summary>
    public static string ToUnified(string resultCode) => resultCode switch
    {
        "0000" => "",
        "0051" => UnifiedErrors.InsufficientFunds,
        "0005" or "0034" or "0043" => UnifiedErrors.CardDeclined,
        "0054" => UnifiedErrors.ExpiredCard,
        "0014" or "0015" => UnifiedErrors.InvalidCard,
        "0013" => UnifiedErrors.InvalidAmount,
        "0057" or "0058" or "0062" => UnifiedErrors.NotPermitted,
        "0061" or "0065" => UnifiedErrors.LimitExceeded,
        "0091" or "0096" => UnifiedErrors.IssuerUnavailable,
        _ => UnifiedErrors.ProcessingError,
    };

    private static string? Value(XElement root, string name)
        => root.Descendants(name).FirstOrDefault()?.Value is { Length: > 0 } value ? value : null;
}
