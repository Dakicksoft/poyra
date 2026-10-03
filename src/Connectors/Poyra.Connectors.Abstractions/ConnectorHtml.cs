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
    public static (string ActionUrl, Dictionary<string, string> Fields)? ExtractForm(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var form = FormTag().Match(html);
        if (!form.Success) return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in InputTag().Matches(form.Value))
            fields[input.Groups["ad"].Value] = WebUtility.HtmlDecode(input.Groups["deger"].Value);

        return (WebUtility.HtmlDecode(form.Groups["action"].Value), fields);
    }

    [GeneratedRegex("""<form[^>]*action=["'](?<action>[^"']+)["'][^>]*>.*?</form>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FormTag();

    [GeneratedRegex("""<input[^>]*name=["'](?<ad>[^"']+)["'][^>]*value=["'](?<deger>[^"']*)["']""",
        RegexOptions.IgnoreCase)]
    private static partial Regex InputTag();
}
