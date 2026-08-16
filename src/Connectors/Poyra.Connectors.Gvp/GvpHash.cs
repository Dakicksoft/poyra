using System.Security.Cryptography;
using System.Text;

namespace Poyra.Connectors.Gvp;

/// <summary>
/// Garanti GVP hash şemaları (apiversion 512):
/// - HashedPassword = SHA1(provizyonŞifresi + terminalId 9 haneye sıfır dolgulu) HEX BÜYÜK
/// - 3D isteği: SHA512(terminalId + orderId + tutarKuruş + paraKodu + successUrl + errorUrl
///   + işlemTipi + taksit + storeKey + HashedPassword) HEX BÜYÜK
/// - Dönüş: hashparams'ta adı geçen alanların DEĞERLERİ sırayla birleştirilir (hashparamsval),
///   sonuna storeKey eklenip özetlenir; 'hash' ile karşılaştırılır (SHA512/SHA1 uzunluktan seçilir).
/// </summary>
public static class GvpHash
{
    public static string PadTerminalId(string terminalId) => terminalId.PadLeft(9, '0');

    public static string HashedPassword(string provisionPassword, string terminalId)
        => Convert.ToHexString(SHA1.HashData(
            Encoding.UTF8.GetBytes(provisionPassword + PadTerminalId(terminalId))));

    public static string ThreeDsRequestHash(
        string terminalId, string orderId, string amountMinor, string currencyCode,
        string successUrl, string errorUrl, string txnType, string installments,
        string storeKey, string hashedPassword)
        => Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(
            terminalId + orderId + amountMinor + currencyCode + successUrl + errorUrl
            + txnType + installments + storeKey + hashedPassword)));

    public static bool ValidateCallback(IReadOnlyDictionary<string, string> form, string storeKey)
    {
        var posted = Get(form, "hash");
        var hashParams = Get(form, "hashparams");
        if (string.IsNullOrEmpty(posted) || string.IsNullOrEmpty(hashParams))
            return false;

        var builder = new StringBuilder();
        foreach (var name in hashParams.Split(':', StringSplitOptions.RemoveEmptyEntries))
            builder.Append(Get(form, name) ?? "");
        builder.Append(storeKey);

        var plain = Encoding.UTF8.GetBytes(builder.ToString());

        // Uzunluğa göre algoritma: 128 hex → SHA512, 40 hex → SHA1, 88 base64 → SHA512(b64)
        string expected = posted.Length switch
        {
            128 => Convert.ToHexString(SHA512.HashData(plain)),
            40 => Convert.ToHexString(SHA1.HashData(plain)),
            _ => Convert.ToBase64String(SHA512.HashData(plain)),
        };

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(posted.ToUpperInvariant()),
            Encoding.UTF8.GetBytes(expected.Length == posted.Length ? expected.ToUpperInvariant() : expected));
    }

    public static string ApiRequestHash(
        string orderId, string terminalId, string amountMinor, string currencyCode, string hashedPassword)
        => Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(
            orderId + terminalId + amountMinor + currencyCode + hashedPassword)));

    private static string? Get(IReadOnlyDictionary<string, string> form, string key)
        => form.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
}
