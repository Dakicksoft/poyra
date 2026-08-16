using System.Security.Cryptography;
using System.Text;

namespace Poyra.Connectors.Posnet;

/// <summary>
/// Posnet (Yapı Kredi) MAC hesabı. İki aşamalıdır ve SIRA ÖNEMLİDİR:
///   firstHash = Base64( SHA-256( encKey + ";" + terminalId ) )
///   mac       = Base64( SHA-256( orderId + ";" + amount + ";" + currency + ";" + merchantId + ";" + firstHash ) )
///
/// Dönüş (oosTranData) doğrulamasında merchantId yerine mac değeri farklı bir alan
/// dizisinden türetilir; banka dokümanı sürümüne göre alan sırası değişebildiği için
/// TODO(cert) işaretlidir — sertifikasyon testinde birebir doğrulanacaktır.
///
/// Not: SHA-256 çıktısı Base64'tür, hex DEĞİL (NestPay ver3 ile karıştırılmamalı).
/// </summary>
public static class PosnetMac
{
    public static string FirstHash(string encKey, string terminalId)
        => Base64Sha256($"{encKey};{terminalId}");

    public static string Mac(string orderId, string amount, string currency, string merchantId, string firstHash)
        => Base64Sha256($"{orderId};{amount};{currency};{merchantId};{firstHash}");

    /// <summary>
    /// Dönen MAC'in doğrulaması: banka aynı alanları kendi tarafında birleştirip imzalar.
    /// TODO(cert): alan sırası YKB sertifikasyonunda teyit edilecek.
    /// </summary>
    public static bool ValidateResponse(
        string? returnedMac, string mdStatus, string orderId, string amount,
        string currency, string merchantId, string encKey, string terminalId)
    {
        if (string.IsNullOrEmpty(returnedMac))
            return false;

        var expected = Base64Sha256(
            $"{mdStatus};{orderId};{amount};{currency};{merchantId};{FirstHash(encKey, terminalId)}");

        // Sabit süreli karşılaştırma: MAC doğrulaması zamanlama sızıntısına açık olmamalı
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(returnedMac), Encoding.UTF8.GetBytes(expected));
    }

    public static string Amount(long amountMinor) => amountMinor.ToString();

    /// <summary>
    /// Posnet sipariş numarası SABİT 20 KARAKTERDİR ve alfanümeriktir. Bizim att_… kimliğimiz
    /// daha uzundur; başı kırpılmaz — SONU alınır, çünkü Guid v7'nin ayırt edici bitleri sondadır
    /// (baştan kırpmak aynı milisaniyedeki denemeleri çakıştırır).
    /// </summary>
    public static string OrderId(string attemptPublicId)
    {
        var bare = attemptPublicId.StartsWith("att_", StringComparison.Ordinal)
            ? attemptPublicId[4..]
            : attemptPublicId;

        return bare.Length >= 20 ? bare[^20..] : bare.PadLeft(20, '0');
    }

    private static string Base64Sha256(string value)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
