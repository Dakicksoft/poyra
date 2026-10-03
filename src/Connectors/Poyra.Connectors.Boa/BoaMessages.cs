using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Boa;


public static class BoaMessages
{
    public const string TryCurrencyCode = "0949";

    public static string Amount(long amountMinor)
        => amountMinor.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 3D istek hash'i. Parola önce TEK BAŞINA SHA1+base64 edilir, sonra diğer alanlarla
    /// birleştirilip yeniden SHA1+base64 alınır.
    ///
    /// <b>TODO(cert):</b> hem sıra hem bu iki aşamalı yapı bankayla doğrulanmalı.
    /// </summary>
    public static string RequestHash(
        string merchantId, string merchantOrderId, string amount,
        string okUrl, string failUrl, string userName, string password)
        => Sha1Base64(string.Concat(
            merchantId, merchantOrderId, amount, okUrl, failUrl, userName, HashedPassword(password)));

    /// <summary>
    /// Provizyon (ikinci adım) hash'i. 3D hash'inden FARKLIDIR: OkUrl/FailUrl girmez.
    /// Aynı formülü kullanmak provizyon çağrısının sessizce reddedilmesi demektir.
    /// </summary>
    public static string ProvisionHash(
        string merchantId, string merchantOrderId, string amount, string userName, string password)
        => Sha1Base64(string.Concat(
            merchantId, merchantOrderId, amount, userName, HashedPassword(password)));

    /// <summary>Bankaya ayrıca gönderilen ön-hash'lenmiş parola.</summary>
    public static string HashedPassword(string password) => Sha1Base64(password);

    /// <summary>
    /// Provizyon isteği gövdesi. 3D dönüşündeki <c>MD</c> değeri bankaya geri verilir;
    /// tahsilatı kesinleştiren çağrı budur. Kök eleman bankaya göre değişir.
    /// </summary>
    public static string ProvisionRequestXml(
        string rootElement, string extraDataElement, string merchantId, string customerId, string userName,
        string merchantOrderId, string amount, int installmentCount, string md, string hashData)
        => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <{rootElement} xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <APIVersion>1.0.0</APIVersion>
              <HashData>{EscapeXml(hashData)}</HashData>
              <MerchantId>{EscapeXml(merchantId)}</MerchantId>
              <CustomerId>{EscapeXml(customerId)}</CustomerId>
              <UserName>{EscapeXml(userName)}</UserName>
              <TransactionType>Sale</TransactionType>
              <InstallmentCount>{installmentCount}</InstallmentCount>
              <Amount>{EscapeXml(amount)}</Amount>
              <CurrencyCode>{TryCurrencyCode}</CurrencyCode>
              <MerchantOrderId>{EscapeXml(merchantOrderId)}</MerchantOrderId>
              <TransactionSecurity>3</TransactionSecurity>
              <{extraDataElement}>
                <AdditionalData>
                  <Key>MD</Key>
                  <Data>{EscapeXml(md)}</Data>
                </AdditionalData>
              </{extraDataElement}>
            </{rootElement}>
            """;


    public static string CancelXml(
        string rootElement, string merchantId, string customerId, string userName,
        string hashedPassword, string merchantOrderId, string orderId, string amount, string hashData)
        => Envelope(rootElement, hashData, merchantId, customerId, userName, hashedPassword, $"""
              <MerchantOrderId>{EscapeXml(merchantOrderId)}</MerchantOrderId>
              <Amount>{EscapeXml(amount)}</Amount>
              <OrderId>{EscapeXml(orderId)}</OrderId>
              <PaymentType>1</PaymentType>
            """);


    public static string PartialRefundXml(
        string rootElement, string merchantId, string customerId, string userName,
        string hashedPassword, string merchantOrderId, string orderId, string amount, string hashData)
        => Envelope(rootElement, hashData, merchantId, customerId, userName, hashedPassword, $"""
              <OrderId>{EscapeXml(orderId)}</OrderId>
              <MerchantOrderId>{EscapeXml(merchantOrderId)}</MerchantOrderId>
              <Amount>{EscapeXml(amount)}</Amount>
              <DisplayAmount>{EscapeXml(amount)}</DisplayAmount>
            """);

    private static string Envelope(
        string rootElement, string hashData, string merchantId, string customerId,
        string userName, string hashedPassword, string body)
        => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <{rootElement} xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <HashData>{EscapeXml(hashData)}</HashData>
              <MerchantId>{EscapeXml(merchantId)}</MerchantId>
              <SubMerchantId>0</SubMerchantId>
              <CustomerId>{EscapeXml(customerId)}</CustomerId>
              <UserName>{EscapeXml(userName)}</UserName>
              <HashPassword>{EscapeXml(hashedPassword)}</HashPassword>
            {body}
            </{rootElement}>
            """;

    private static string EscapeXml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    /// <summary>
    /// Dönüş başarılı mı: <c>ResponseCode</c> "00" olmalı.
    ///
    /// <b>TODO(cert):</b> banka bazı senaryolarda farklı başarı kodları döndürebilir;
    /// liste bankadan alınmalı.
    /// </summary>
    public static bool IsApproved(IReadOnlyDictionary<string, string> form)
        => IsApprovedCode(form.GetValueOrDefault("ResponseCode"));

    public static bool IsApprovedCode(string? responseCode) => responseCode == "00";

    /// <summary>
    /// Banka XML'ini düz sözlüğe çevirir (yaprak düğüm adı → değer). Aynı adlı düğümlerde
    /// İLKİ kazanır; aradığımız alanlar köke yakındır. Bozuk/boş gövde boş sözlük döner —
    /// çağıran "onaylanmadı" sayar (fail closed).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(xml)) return result;

        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root is null) return result;

            foreach (var node in root.DescendantsAndSelf())
            {
                if (node.HasElements) continue;
                result.TryAdd(node.Name.LocalName, node.Value.Trim());
            }
        }
        catch (System.Xml.XmlException)
        {
            // Banka XML yerine HTML hata sayfası döndürebilir — susup boş dönmek,
            // yanlış ayrıştırılmış bir "00" üretmekten iyidir.
        }

        return result;
    }

    public static string UnifiedError(string? responseCode) => responseCode switch
    {
        "51" => UnifiedErrors.InsufficientFunds,
        "54" => UnifiedErrors.ExpiredCard,
        "14" => UnifiedErrors.InvalidCard,
        "57" => UnifiedErrors.NotPermitted,
        "61" => UnifiedErrors.LimitExceeded,
        null or "" => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined,
    };

    private static string Sha1Base64(string value)
        => Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
}
