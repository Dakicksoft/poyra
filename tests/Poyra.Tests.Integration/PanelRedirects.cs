using System.Net;
using System.Text.RegularExpressions;
using Shouldly;

namespace Poyra.Tests.Integration;

/// <summary>
/// Panel flash mesajları artık imzalı taşınır (?sonuc=&lt;blob&gt;) — URL'den okunamaz.
/// Testler mesajı KULLANICININ GÖRDÜĞÜ yerden doğrular: redirect izlenir, sayfadaki
/// alert kutularının metni döndürülür. (Bu, eski URL metni doğrulamasından daha da
/// gerçekçi bir uçtan uca doğrulamadır.)
/// </summary>
public static class PanelRedirects
{
    public static async Task<string> RevealAsync(HttpClient panel, HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var html = await panel.GetStringAsync(response.Headers.Location!.ToString());
        return AlertText(html);
    }

    /// <summary>Sayfadaki tüm .alert kutularının düz metni (banner dahil, ayraçlı).</summary>
    public static string AlertText(string html)
    {
        var alerts = Regex.Matches(html,
                "<div class=\"alert [^\"]*\"[^>]*>(.*?)</div>", RegexOptions.Singleline)
            .Select(m => WebUtility.HtmlDecode(
                Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")).Trim());
        return string.Join(" || ", alerts);
    }
}
