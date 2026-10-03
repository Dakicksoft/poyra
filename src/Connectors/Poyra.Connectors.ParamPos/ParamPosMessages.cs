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
        string clientCode, string guid, string installment, string amount, string totalAmount, string orderNumber)
        => Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(
            string.Concat(clientCode, guid, installment, amount, totalAmount, orderNumber))));

    public static bool Succeeded(string? result)
        => int.TryParse(result, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0;


    public static string Envelope(string operation, string guid, IReadOnlyDictionary<string, string> fields,
        string clientCode, string clientUsername, string clientPassword)
    {
        var body = new StringBuilder();
        foreach (var (name, value) in fields)
            body.Append(CultureInfo.InvariantCulture, $"      <{name}>{EscapeXml(value)}</{name}>\n");

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <{operation} xmlns="{Ns}">
                  <G>
                    <CLIENT_CODE>{EscapeXml(clientCode)}</CLIENT_CODE>
                    <CLIENT_USERNAME>{EscapeXml(clientUsername)}</CLIENT_USERNAME>
                    <CLIENT_PASSWORD>{EscapeXml(clientPassword)}</CLIENT_PASSWORD>
                  </G>
                  <GUID>{EscapeXml(guid)}</GUID>
            {body}    </{operation}>
              </soap:Body>
            </soap:Envelope>
            """;
    }

    public static IReadOnlyDictionary<string, string> Parse(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(xml)) return result;

        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root is null) return result;

            foreach (var node in root.Descendants())
            {
                if (node.HasElements) continue;
                result.TryAdd(node.Name.LocalName, node.Value.Trim());
            }
        }
        catch (System.Xml.XmlException)
        {
            // Sağlayıcı XML yerine HTML hata sayfası döndürebilir — susup boş dönmek,
            // yanlış ayrıştırılmış bir "başarılı" üretmekten iyidir.
        }

        return result;
    }

    public static string UnifiedError(string? result, string? mdStatus) => (result, mdStatus) switch
    {
        (_, "0") => UnifiedErrors.ThreeDsFailed,
        (_, "2" or "3" or "4") => UnifiedErrors.ThreeDsUnavailable,
        (_, "5" or "6" or "7" or "8") => UnifiedErrors.ThreeDsFailed,
        ("-1" or "-2", _) => UnifiedErrors.ProcessingError,
        (null or "", _) => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined,
    };

    private static string EscapeXml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
