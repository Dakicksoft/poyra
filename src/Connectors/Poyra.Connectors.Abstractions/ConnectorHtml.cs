using System.Net;
using System.Text.RegularExpressions;

namespace Poyra.Connectors.Abstractions;

/// <summary>
/// Bazı sağlayıcılar 3D adımını "adres + alanlar" olarak değil, kendi kendine gönderilen
/// HAZIR HTML olarak döndürür (CCPayment, İyzico). Poyra'nın modeli adres+alan ister —
/// formu o HTML'den çıkarmak birden çok konnektörün ortak ihtiyacı olduğu için burada durur:
/// ayrıştırma güvenliğe değen bir iştir, her konnektörde ayrı kopyası olmamalı.
/// </summary>
public static partial class ConnectorHtml
{
    /// <summary>İlk formun action adresi ve input alanları; form yoksa <c>null</c>.</summary>
    public static (string ActionUrl, Dictionary<string, string> Fields)? FormuCikar(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var form = FormEtiketi().Match(html);
        if (!form.Success) return null;

        var alanlar = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match girdi in Girdi().Matches(form.Value))
            alanlar[girdi.Groups["ad"].Value] = WebUtility.HtmlDecode(girdi.Groups["deger"].Value);

        return (WebUtility.HtmlDecode(form.Groups["action"].Value), alanlar);
    }

    [GeneratedRegex("""<form[^>]*action=["'](?<action>[^"']+)["'][^>]*>.*?</form>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FormEtiketi();

    [GeneratedRegex("""<input[^>]*name=["'](?<ad>[^"']+)["'][^>]*value=["'](?<deger>[^"']*)["']""",
        RegexOptions.IgnoreCase)]
    private static partial Regex Girdi();
}
