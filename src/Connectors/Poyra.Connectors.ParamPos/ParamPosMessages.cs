using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.ParamPos;


public static class ParamPosMessages
{
    public const string Ns = "https://turkpos.com.tr/";

    public static string Amount(long amountMinor)
        => (amountMinor / 100m).ToString("0.00", CultureInfo.GetCultureInfo("tr-TR"));

    /// <summary>
    /// İstek hash'i:
    /// <c>CLIENT_CODE + GUID + taksit + tutar + toplamTutar + siparisNo</c> → SHA1 → Base64.
    /// </summary>
    public static string RequestHash(
        string clientCode, string guid, string taksit, string tutar, string toplamTutar, string siparisNo)
        => Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(
            string.Concat(clientCode, guid, taksit, tutar, toplamTutar, siparisNo))));

    public static bool Basarili(string? sonuc)
        => int.TryParse(sonuc, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deger) && deger > 0;


    public static string Zarf(string islem, string guid, IReadOnlyDictionary<string, string> alanlar,
        string clientCode, string clientUsername, string clientPassword)
    {
        var govde = new StringBuilder();
        foreach (var (ad, deger) in alanlar)
            govde.Append(CultureInfo.InvariantCulture, $"      <{ad}>{Kacir(deger)}</{ad}>\n");

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <{islem} xmlns="{Ns}">
                  <G>
                    <CLIENT_CODE>{Kacir(clientCode)}</CLIENT_CODE>
                    <CLIENT_USERNAME>{Kacir(clientUsername)}</CLIENT_USERNAME>
                    <CLIENT_PASSWORD>{Kacir(clientPassword)}</CLIENT_PASSWORD>
                  </G>
                  <GUID>{Kacir(guid)}</GUID>
            {govde}    </{islem}>
              </soap:Body>
            </soap:Envelope>
            """;
    }

    public static IReadOnlyDictionary<string, string> Oku(string xml)
    {
        var sonuc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(xml)) return sonuc;

        try
        {
            var kok = XDocument.Parse(xml).Root;
            if (kok is null) return sonuc;

            foreach (var dugum in kok.Descendants())
            {
                if (dugum.HasElements) continue;
                sonuc.TryAdd(dugum.Name.LocalName, dugum.Value.Trim());
            }
        }
        catch (System.Xml.XmlException)
        {
            // Sağlayıcı XML yerine HTML hata sayfası döndürebilir — susup boş dönmek,
            // yanlış ayrıştırılmış bir "başarılı" üretmekten iyidir.
        }

        return sonuc;
    }

    public static string UnifiedError(string? sonuc, string? mdStatus) => (sonuc, mdStatus) switch
    {
        (_, "0") => UnifiedErrors.ThreeDsFailed,
        (_, "2" or "3" or "4") => UnifiedErrors.ThreeDsUnavailable,
        (_, "5" or "6" or "7" or "8") => UnifiedErrors.ThreeDsFailed,
        ("-1" or "-2", _) => UnifiedErrors.ProcessingError,
        (null or "", _) => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined,
    };

    private static string Kacir(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
