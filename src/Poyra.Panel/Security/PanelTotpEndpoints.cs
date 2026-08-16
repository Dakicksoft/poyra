using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Poyra.Modules.Tenancy.Features.Auth;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using QRCoder;

namespace Poyra.Panel.Security;

public static class PanelTotpEndpoints
{
    public const string PendingCookie = "poyra_2fa";
    public const string DeviceCookie = "poyra_cihaz";
    private const string ProtectorPurpose = "Poyra.Panel.Totp.PendingLogin";
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);

    public sealed record PendingLogin(
        Guid UserId, string Email, Guid TenantId, string TenantSlug, string TenantName,
        string Role, string? ReturnUrl, long ExpiresUnix);

    public static void IssuePendingLogin(
        HttpContext http, IDataProtectionProvider dp, PendingLogin pending)
    {
        var payload = dp.CreateProtector(ProtectorPurpose)
            .Protect(JsonSerializer.Serialize(pending));
        http.Response.Cookies.Append(PendingCookie, payload, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = PendingLifetime,
            Path = "/giris",
        });
    }

    private static PendingLogin? ReadPendingLogin(HttpContext http, IDataProtectionProvider dp)
    {
        if (http.Request.Cookies[PendingCookie] is not { Length: > 0 } raw)
            return null;

        try
        {
            var pending = JsonSerializer.Deserialize<PendingLogin>(
                dp.CreateProtector(ProtectorPurpose).Unprotect(raw));
            return pending is { } p && p.ExpiresUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                ? pending
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static PendingLogin NewPendingLogin(
        Guid userId, string email, Guid tenantId, string tenantSlug, string tenantName,
        string role, string? returnUrl)
        => new(userId, email, tenantId, tenantSlug, tenantName, role, returnUrl,
            DateTimeOffset.UtcNow.Add(PendingLifetime).ToUnixTimeSeconds());

    public static void MapPanelTotpEndpoints(this WebApplication app)
    {
        app.MapPost("/giris/dogrulama", async (
            [FromForm] string kod,
            [FromForm] string? cihaz,
            HttpContext http,
            IDispatcher dispatcher,
            IDataProtectionProvider dp) =>
        {
            var pending = ReadPendingLogin(http, dp);
            if (pending is null)
                return Results.Redirect("/giris?hata=dogrulama-suresi");

            if (!await dispatcher.Send(new VerifyTotpCommand(pending.UserId, kod)))
                return Results.Redirect("/giris/dogrulama?hata=kod-gecersiz");

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, pending.UserId.ToString()),
                new Claim(ClaimTypes.Email, pending.Email),
                new Claim(PanelClaims.TenantId, pending.TenantId.ToString()),
                new Claim(PanelClaims.TenantSlug, pending.TenantSlug),
                new Claim(PanelClaims.TenantName, pending.TenantName),
                new Claim(PanelClaims.Role, pending.Role),
            ], CookieAuthenticationDefaults.AuthenticationScheme);

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));
            http.Response.Cookies.Delete(PendingCookie, new CookieOptions { Path = "/giris" });

            if (cihaz == "1")
            {
                var token = await dispatcher.Send(new IssueTotpDeviceCommand(pending.UserId));
                http.Response.Cookies.Append(DeviceCookie, token, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = http.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = IssueTotpDeviceHandler.Lifetime,
                    Path = "/",
                });
            }

            var target = pending.ReturnUrl is { Length: > 0 } r
                         && Uri.IsWellFormedUriString(r, UriKind.Relative) ? r : "/";
            return Results.Redirect(target);
        }).WithPanelAntiforgery();

        app.MapPost("/guvenlik/2fa/basla", async (IDispatcher dispatcher) =>
        {
            try
            {
                await dispatcher.Send(new BeginTotpEnrollmentCommand());
                return Results.Redirect("/guvenlik");
            }
            catch (PoyraException ex)
            {
                return GuvenlikRedirect(hata: ex.Code);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/guvenlik/2fa/onayla", async (
            [FromForm] string kod, HttpContext http, IDispatcher dispatcher,
            OneTimeSecretStash stash) =>
        {
            try
            {
                var codes = await dispatcher.Send(new ConfirmTotpEnrollmentCommand(kod));

                http.Response.Cookies.Append(OneTimeSecretStash.CookieName,
                    stash.Put(string.Join("\n", codes.Codes)), new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = http.Request.IsHttps,
                        SameSite = SameSiteMode.Strict,
                        MaxAge = OneTimeSecretStash.Ttl,
                        Path = "/guvenlik",
                    });
                return GuvenlikRedirect(sonuc: "acildi");
            }
            catch (PoyraException ex)
            {
                return GuvenlikRedirect(hata: ex.Code);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/guvenlik/2fa/kapat", async ([FromForm] string kod, IDispatcher dispatcher) =>
        {
            try
            {
                await dispatcher.Send(new DisableTotpCommand(kod));
                return GuvenlikRedirect(sonuc: "kapatildi");
            }
            catch (PoyraException ex)
            {
                return GuvenlikRedirect(hata: ex.Code);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/guvenlik/zorunluluk", async (
            [FromForm] string? gerekli, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Owner))
                return GuvenlikRedirect(hata: "rol-yetersiz");

            await dispatcher.Send(new SetTotpRequirementCommand(gerekli == "1"));
            return GuvenlikRedirect(sonuc: gerekli == "1" ? "zorunlu-acik" : "zorunlu-kapali");
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapGet("/guvenlik/2fa/qr.png", async (IDispatcher dispatcher) =>
        {
            var pending = await dispatcher.Ask(new PendingTotpEnrollmentQuery());
            if (pending is null)
                return Results.NotFound();

            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(pending.OtpauthUri, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(6);
            return Results.File(png, "image/png");
        }).RequireAuthorization();
    }

    private static IResult GuvenlikRedirect(string? sonuc = null, string? hata = null)
        => Results.Redirect(hata is null ? $"/guvenlik?sonuc={sonuc}" : $"/guvenlik?hata={hata}");
}
