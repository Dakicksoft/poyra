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
        string kokEleman, string ekVeriEleman, string merchantId, string customerId, string userName,
        string merchantOrderId, string amount, int installmentCount, string md, string hashData)
        => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <{kokEleman} xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <APIVersion>1.0.0</APIVersion>
              <HashData>{Kacir(hashData)}</HashData>
              <MerchantId>{Kacir(merchantId)}</MerchantId>
              <CustomerId>{Kacir(customerId)}</CustomerId>
              <UserName>{Kacir(userName)}</UserName>
              <TransactionType>Sale</TransactionType>
              <InstallmentCount>{installmentCount}</InstallmentCount>
              <Amount>{Kacir(amount)}</Amount>
              <CurrencyCode>{TryCurrencyCode}</CurrencyCode>
              <MerchantOrderId>{Kacir(merchantOrderId)}</MerchantOrderId>
              <TransactionSecurity>3</TransactionSecurity>
              <{ekVeriEleman}>
                <AdditionalData>
                  <Key>MD</Key>
                  <Data>{Kacir(md)}</Data>
                </AdditionalData>
              </{ekVeriEleman}>
            </{kokEleman}>
            """;


    public static string IptalXml(
        string kokEleman, string merchantId, string customerId, string userName,
        string hashedPassword, string merchantOrderId, string orderId, string amount, string hashData)
        => Zarf(kokEleman, hashData, merchantId, customerId, userName, hashedPassword, $"""
              <MerchantOrderId>{Kacir(merchantOrderId)}</MerchantOrderId>
              <Amount>{Kacir(amount)}</Amount>
              <OrderId>{Kacir(orderId)}</OrderId>
              <PaymentType>1</PaymentType>
            """);


    public static string KismiIadeXml(
        string kokEleman, string merchantId, string customerId, string userName,
        string hashedPassword, string merchantOrderId, string orderId, string amount, string hashData)
        => Zarf(kokEleman, hashData, merchantId, customerId, userName, hashedPassword, $"""
              <OrderId>{Kacir(orderId)}</OrderId>
              <MerchantOrderId>{Kacir(merchantOrderId)}</MerchantOrderId>
              <Amount>{Kacir(amount)}</Amount>
              <DisplayAmount>{Kacir(amount)}</DisplayAmount>
            """);

    private static string Zarf(
        string kokEleman, string hashData, string merchantId, string customerId,
        string userName, string hashedPassword, string govde)
        => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <{kokEleman} xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <HashData>{Kacir(hashData)}</HashData>
              <MerchantId>{Kacir(merchantId)}</MerchantId>
              <SubMerchantId>0</SubMerchantId>
              <CustomerId>{Kacir(customerId)}</CustomerId>
              <UserName>{Kacir(userName)}</UserName>
              <HashPassword>{Kacir(hashedPassword)}</HashPassword>
            {govde}
            </{kokEleman}>
            """;

    private static string Kacir(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

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
    public static IReadOnlyDictionary<string, string> Oku(string xml)
    {
        var sonuc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(xml)) return sonuc;

        try
        {
            var kok = XDocument.Parse(xml).Root;
            if (kok is null) return sonuc;

            foreach (var dugum in kok.DescendantsAndSelf())
            {
                if (dugum.HasElements) continue;
                sonuc.TryAdd(dugum.Name.LocalName, dugum.Value.Trim());
            }
        }
        catch (System.Xml.XmlException)
        {
            // Banka XML yerine HTML hata sayfası döndürebilir — susup boş dönmek,
            // yanlış ayrıştırılmış bir "00" üretmekten iyidir.
        }

        return sonuc;
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
