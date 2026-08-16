using Poyra.Modules.PaymentLinks.Contracts;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.SharedKernel.Domain;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Payments.Features.ConfirmPayment;
using Poyra.Modules.Payments.Features.CreatePayment;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Checkout;

public static class CheckoutEndpoints
{
    public static void MapCheckoutEndpoints(this WebApplication app)
    {
        app.MapPost("/l/{slug}/ode", async (
            string slug,
            HttpContext http,
            ICheckoutLinkResolver links,
            IDispatcher dispatcher,
            TenantContext tenant,
            IConfiguration configuration) =>
        {
            var form = await ReadFormAsync(http);

            var link = await links.ResolveAsync(slug, http.RequestAborted);
            if (link is null)
                return Results.NotFound();

            if (link.UnavailableReason is { } reason)
                return Results.Redirect($"/l/{slug}?hata={Uri.EscapeDataString(reason)}");

            var amount = link.AmountMinor ?? UserInput.ToKurus(form("amountLira"));
            if (amount <= 0)
                return Results.Redirect($"/l/{slug}?hata={Uri.EscapeDataString("Geçerli bir tutar girin.")}");

            var bin = form("bin");
            var installmentCount = Math.Clamp(
                UserInput.ToInt(form("installments"), 1), 1, link.MaxInstallments);

            tenant.Set(link.TenantId); // RLS bağlamı: bundan sonrası normal akış
            var baseUrl = configuration["Poyra:CheckoutBaseUrl"] ?? $"{http.Request.Scheme}://{http.Request.Host}";

            try
            {
                var created = await dispatcher.Send(new CreatePaymentCommand(
                    amount, link.Currency, link.Description, installmentCount,
                    ReturnUrl: $"{baseUrl.TrimEnd('/')}/l/{slug}/sonuc"));

                await links.RegisterAttemptAsync(
                    link.TenantId, link.LinkId, created.Id, amount, http.RequestAborted);

                var confirmed = await dispatcher.Send(
                    new ConfirmPaymentCommand(created.Id, null, null, bin));

                if (confirmed.Status != PaymentStatus.RequiresAction || confirmed.NextAction is null)
                {
                    var error = confirmed.LastError?.Code ?? "payment.failed";
                    return Results.Redirect(
                        $"/l/{slug}/sonuc?status=failed&poyra_payment_id={confirmed.Id}&kod={error}");
                }

                if (confirmed.NextAction.Method == "GET" && confirmed.NextAction.Fields.Count == 0)
                    return Results.Redirect(confirmed.NextAction.Url);

                return Results.Content(BuildAutoSubmitForm(confirmed.NextAction), "text/html; charset=utf-8");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return Results.Redirect($"/l/{slug}?hata={Uri.EscapeDataString(ex.Message)}");
            }
        }).DisableAntiforgery();

        app.MapPost("/l/{slug}/taksit", async (
            string slug,
            HttpContext http,
            ICheckoutLinkResolver links) =>
        {
            var form = await ReadFormAsync(http);

            var link = await links.ResolveAsync(slug, http.RequestAborted);
            if (link is null)
                return Results.NotFound();

            var amount = link.AmountMinor ?? UserInput.ToKurus(form("amountLira"));
            return Results.Redirect(
                $"/l/{slug}?bin={Uri.EscapeDataString(form("bin") ?? "")}" +
                (link.AmountMinor is null ? $"&tutar={amount}" : ""));
        }).DisableAntiforgery();
    }

    public static void MapBrandingEndpoints(this WebApplication app)
    {
        app.MapGet("/l/{slug}/logo", async (
            string slug, HttpContext http, ICheckoutLinkResolver links, ITenantBrandingSource branding) =>
        {
            var link = await links.ResolveAsync(slug, http.RequestAborted);
            if (link is null)
                return Results.NotFound();

            var logo = await branding.GetLogoAsync(link.TenantId, http.RequestAborted);
            if (logo is not { } file)
                return Results.NotFound();

            // Logo nadiren değişir; müşterinin ilk boyası hızlansın
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.File(file.Bytes, file.ContentType);
        });
    }

    private static async Task<Func<string, string?>> ReadFormAsync(HttpContext http)
    {
        if (!http.Request.HasFormContentType)
            return _ => null;

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        return key => form.TryGetValue(key, out var value) ? value.ToString() : null;
    }

    private static string BuildAutoSubmitForm(Poyra.Modules.Payments.Features.NextActionDto action)
    {
        var fields = string.Join("\n", action.Fields.Select(f =>
            $"""    <input type="hidden" name="{Escape(f.Key)}" value="{Escape(f.Value)}" />"""));

        return $"""
            <!DOCTYPE html>
            <html lang="tr"><head><meta charset="utf-8" />
            <title>Bankaya yönlendiriliyorsunuz…</title>
            <link rel="stylesheet" href="/checkout.css" /></head>
            <body class="redirecting">
              <div class="card center">
                <div class="spinner" aria-hidden="true"></div>
                <p>Bankanızın güvenli ödeme sayfasına yönlendiriliyorsunuz…</p>
                <p class="hint">Sayfayı kapatmayın.</p>
              </div>
              <form id="f" method="{Escape(action.Method)}" action="{Escape(action.Url)}">
            {fields}
                <noscript><button type="submit">Devam et</button></noscript>
              </form>
              <script>document.getElementById('f').submit();</script>
            </body></html>
            """;
    }

    private static string Escape(string value)
        => System.Net.WebUtility.HtmlEncode(value);
}
