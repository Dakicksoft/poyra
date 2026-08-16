using System.Security.Cryptography;
using System.Text;
using Hangfire.Dashboard;

namespace Poyra.Api.Infrastructure;

public sealed class PlatformKeyDashboardFilter(string? adminKey, bool allowAll) : IDashboardAuthorizationFilter
{
    private const string CookieName = "poyra_hangfire";

    public bool Authorize(DashboardContext context)
    {
        if (allowAll)
            return true;
        if (string.IsNullOrEmpty(adminKey))
            return false;

        var http = context.GetHttpContext();
        var expectedCookie = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(adminKey)));

        if (http.Request.Cookies[CookieName] == expectedCookie)
            return true;

        var provided = http.Request.Query["key"].ToString();
        if (provided.Length > 0 && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(adminKey)))
        {
            http.Response.Cookies.Append(CookieName, expectedCookie, new CookieOptions
            {
                HttpOnly = true,
                Path = "/hangfire",
                SameSite = SameSiteMode.Strict,
            });
            return true;
        }

        return false;
    }
}
