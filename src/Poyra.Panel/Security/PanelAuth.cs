using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Poyra.Modules.Tenancy.Features.Auth;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Domain;

namespace Poyra.Panel.Security;

public static class PanelClaims
{
    public const string TenantId = "poyra:tid";
    public const string TenantSlug = "poyra:slug";
    public const string TenantName = "poyra:tenant_name";
    public const string Role = "poyra:role";
}

public sealed class PanelTenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, UserContext user)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated == true
            && Guid.TryParse(principal.FindFirstValue(PanelClaims.TenantId), out var tenantId)
            && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && TenantRoleMap.FromDb.TryGetValue(principal.FindFirstValue(PanelClaims.Role) ?? "", out var role))
        {
            tenant.Set(tenantId);
            user.SetUser(userId, principal.FindFirstValue(ClaimTypes.Email) ?? "", role);
        }

        await next(context);
    }
}

public sealed class PanelPrincipalBinder(IHttpContextAccessor accessor)
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public string Email => Principal?.FindFirstValue(ClaimTypes.Email) ?? "";
    public string TenantName => Principal?.FindFirstValue(PanelClaims.TenantName) ?? "";
    public string TenantSlug => Principal?.FindFirstValue(PanelClaims.TenantSlug) ?? "";
    public string Role => Principal?.FindFirstValue(PanelClaims.Role) ?? "";

    public Guid? TenantId
        => Guid.TryParse(Principal?.FindFirstValue(PanelClaims.TenantId), out var id) ? id : null;

    public Guid? UserId
        => Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public bool HasRank(TenantRole minimum)
        => TenantRoleMap.FromDb.TryGetValue(Role, out var role) && role >= minimum;
}

public static class PanelAuthEndpoints
{

    public static void MapPanelAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/giris", async (
            [FromForm] string email,
            [FromForm] string password,
            [FromForm] string? tenantSlug,
            [FromForm] string? returnUrl,
            HttpContext http,
            IDispatcher dispatcher,
            Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dataProtection) =>
        {
            try
            {
                var tokens = await dispatcher.Send(new LoginCommand(email, password, tenantSlug));
                var normalizedEmail = TurkishText.NormalizeEmail(email);

                var target = returnUrl is { Length: > 0 } && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
                    ? returnUrl
                    : "/";

                var totpEnabled = await dispatcher.Ask(new UserTotpEnabledQuery(tokens.UserId));
                if (totpEnabled)
                {
                    var device = http.Request.Cookies[PanelTotpEndpoints.DeviceCookie];
                    var trusted = device is { Length: > 0 }
                        && await dispatcher.Ask(new CheckTotpDeviceQuery(tokens.UserId, device));
                    if (!trusted)
                    {
                        PanelTotpEndpoints.IssuePendingLogin(http, dataProtection,
                            PanelTotpEndpoints.NewPendingLogin(
                                tokens.UserId, normalizedEmail, tokens.Tenant.Id,
                                tokens.Tenant.Slug, tokens.Tenant.Name, tokens.Role, target));
                        return Results.Redirect("/giris/dogrulama");
                    }
                }

                var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, tokens.UserId.ToString()),
                    new Claim(ClaimTypes.Email, normalizedEmail),
                    new Claim(PanelClaims.TenantId, tokens.Tenant.Id.ToString()),
                    new Claim(PanelClaims.TenantSlug, tokens.Tenant.Slug),
                    new Claim(PanelClaims.TenantName, tokens.Tenant.Name),
                    new Claim(PanelClaims.Role, tokens.Role),
                ], CookieAuthenticationDefaults.AuthenticationScheme);

                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity));


                if (!totpEnabled
                    && tokens.Role is "owner" or "admin"
                    && await dispatcher.Ask(new TenantRequiresTotpQuery(tokens.Tenant.Id)))
                    return Results.Redirect("/guvenlik?zorunlu=1");

                return Results.Redirect(target);
            }
            catch (PoyraException ex)
            {
                return Results.Redirect(
                    $"/giris?hata={AuthErrorCode(ex)}&eposta={Uri.EscapeDataString(email)}");
            }
        }).WithPanelAntiforgery();

        app.MapPost("/cikis", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/giris");
        }).WithPanelAntiforgery();

        app.MapPost("/parola-unuttum", async ([FromForm] string email, IDispatcher dispatcher) =>
        {
            try
            {
                await dispatcher.Send(new ForgotPasswordCommand(email));
                return Results.Redirect("/giris?sonuc=sifirlama-gonderildi");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                var kod = ex is FluentValidation.ValidationException
                    ? "gecersiz-eposta"
                    : AuthErrorCode(ex);
                return Results.Redirect($"/parola-unuttum?hata={kod}");
            }
        }).WithPanelAntiforgery();

        app.MapPost("/parola-sifirla", async (
            [FromForm] string belirtec,
            [FromForm] string password,
            [FromForm] string passwordRepeat,
            HttpContext http,
            IDispatcher dispatcher) =>
        {
            if (password != passwordRepeat)
                return ResetError(belirtec, "parola-eslesmiyor");

            try
            {
                await dispatcher.Send(new ResetPasswordCommand(belirtec, password));

                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                return Results.Redirect("/giris?sonuc=parola-guncellendi");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return ResetError(belirtec, ex is FluentValidation.ValidationException
                    ? "parola-kurali"
                    : AuthErrorCode(ex));
            }
        }).WithPanelAntiforgery();

        app.MapPost("/eposta-dogrula", async ([FromForm] string belirtec, IDispatcher dispatcher) =>
        {
            try
            {
                await dispatcher.Send(new VerifyEmailCommand(belirtec));
                return Results.Redirect("/eposta-dogrula?sonuc=dogrulandi");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return Results.Redirect($"/eposta-dogrula?hata={AuthErrorCode(ex)}");
            }
        }).WithPanelAntiforgery();

        app.MapPost("/eposta-dogrula/gonder", async (HttpContext http, IDispatcher dispatcher) =>
        {
            try
            {
                var result = await dispatcher.Send(new SendVerificationCommand());
                return Results.Redirect($"/?sonuc={Uri.EscapeDataString(result.Message)}");
            }
            catch (PoyraException ex)
            {
                return Results.Redirect($"/?hata={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization().WithPanelAntiforgery();
    }

    private static IResult ResetError(string token, string code)
        => Results.Redirect($"/parola-sifirla?belirtec={Uri.EscapeDataString(token)}&hata={code}");

    private static string AuthErrorCode(Exception ex) => ex switch
    {
        PoyraException pe => pe.Code switch
        {
            "auth.invalid_credentials" => "gecersiz-kimlik",
            "auth.no_membership" => "uyelik-yok",
            "auth.tenant_mismatch" => "isyeri-uyusmuyor",
            "auth.tenant_required" => "isyeri-kodu-gerekli",
            "auth.reset_token_invalid" => "belirtec-gecersiz",
            "auth.verify_token_invalid" => "belirtec-gecersiz",
            _ => "islem-basarisiz",
        },
        _ => "islem-basarisiz",
    };
}
