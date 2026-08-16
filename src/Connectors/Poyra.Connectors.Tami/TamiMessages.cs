using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Tami;
public static class TamiMessages
{
    public static string Amount(long amountMinor)
        => (amountMinor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gövdenin JWS imzası: <c>base64url(başlık).base64url(gövde).base64url(HMACSHA512)</c>.
    ///
    /// Anahtar JWK'daki <c>k</c> alanıdır ve base64url kodludur — düz metin sanıp
    /// olduğu gibi kullanmak imzayı sessizce yanlış üretir.
    /// </summary>
    public static string SecurityHash(string govdeJson, string kid, string jwkKey)
    {
        var baslik = JsonSerializer.Serialize(new { alg = "HS512", kid, typ = "JWT" });
        var imzaGirdisi = $"{Kodla(Encoding.UTF8.GetBytes(baslik))}.{Kodla(Encoding.UTF8.GetBytes(govdeJson))}";

        var imza = HMACSHA512.HashData(
            Coz(jwkKey), Encoding.UTF8.GetBytes(imzaGirdisi));

        return $"{imzaGirdisi}.{Kodla(imza)}";
    }

    public static string UnifiedError(string? paymentStatus, string? mdStatus)
        => (paymentStatus, mdStatus) switch
        {
            (_, "0") => UnifiedErrors.ThreeDsFailed,
            (_, "2" or "3" or "4") => UnifiedErrors.ThreeDsUnavailable,
            (_, "5" or "6" or "7" or "8") => UnifiedErrors.ThreeDsFailed,
            ("SUCCESS", _) => UnifiedErrors.None,
            (null or "", _) => UnifiedErrors.ProcessingError,
            _ => UnifiedErrors.CardDeclined,
        };

    private static string Kodla(byte[] bytes) => Base64Url.EncodeToString(bytes);

    private static byte[] Coz(string metin) => Base64Url.DecodeFromChars(metin);
}
