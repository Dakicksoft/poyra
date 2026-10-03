using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Craftgate;

public static class CraftgateMessages
{

    public static decimal Price(long amountMinor) => amountMinor / 100m;

    /// <summary>Her istekte yenilenir; imzaya ve <c>x-rnd-key</c> başlığına girer.</summary>
    public static string RandomKey()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// <c>x-signature</c> = base64(SHA256(<c>servisAdresi + yol + apiKey + secretKey +
    /// rastgele + gövde</c>)). HMAC değil düz SHA256'dır; gizli anahtar imzalanan
    /// dizenin İÇİNDE durur. Gövdesiz isteklerde (GET) gövde boş dize olur.
    /// </summary>
    public static string Signature(
        string serviceUrl, string path, string apiKey, string secretKey,
        string random, string body)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Concat(serviceUrl, path, apiKey, secretKey, random, body))));

    /// <summary>
    /// 3D adımı base64 kodlu HTML olarak döner; çözülüp içindeki form çıkarılır.
    /// Bozuk base64 <c>null</c> döner — çağıran bunu "sağlayıcı beklenen yanıtı vermedi" sayar.
    /// </summary>
    public static (string ActionUrl, Dictionary<string, string> Fields)? DecodeForm(string? base64Html)
    {
        if (string.IsNullOrWhiteSpace(base64Html)) return null;

        try
        {
            return ConnectorHtml.ExtractForm(Encoding.UTF8.GetString(Convert.FromBase64String(base64Html)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool IsApproved(string? paymentStatus) => paymentStatus == "SUCCESS";

    public static bool IsRefundApproved(string? refundStatus) => refundStatus == "SUCCESS";

    public static string UnifiedError(string? errorGroup, string? errorCode) => errorGroup switch
    {
        "NOT_SUFFICIENT_FUNDS" => UnifiedErrors.InsufficientFunds,
        // Kayıp/çalıntı/el koy: tekrar denenmemeli — dunning bu kodlarda durur.
        "LOST_CARD" or "STOLEN_CARD" or "PICKUP_CARD" => UnifiedErrors.NotPermitted,
        _ => ByCodeRange(errorCode),
    };

    private static string ByCodeRange(string? errorCode)
        => int.TryParse(errorCode, out var code)
            ? code > 10000 ? UnifiedErrors.CardDeclined : UnifiedErrors.ProcessingError
            : UnifiedErrors.ProcessingError;
}
