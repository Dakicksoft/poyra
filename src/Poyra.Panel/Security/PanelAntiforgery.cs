using Microsoft.AspNetCore.Antiforgery;

namespace Poyra.Panel.Security;

/// <summary>
/// Panel form POST'ları için CSRF doğrulaması. Çerçevenin otomatik doğrulaması yalnız
/// [FromForm] bağlamalı uçlarda çalışır ve çıplak 400 döndürür; panel uçlarının çoğu
/// formu ReadFormAsync ile okur. Bu filtre HER uca açıkça uygulanır ve başarısızlıkta
/// kullanıcıyı geldiği sayfaya dostane bir iletiyle geri gönderir (bayat sekme,
/// uygulama yeniden başlatması vb. meşru senaryolarda kullanıcı çıkmaza düşmez).
/// </summary>
public sealed class AntiforgeryEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // YALNIZ entegrasyon testleri kapatır (form kurulumunu değil işlevi test ederler);
        // üretimde ayar yoktur ve varsayılan ZORLAMADIR. Zorlamanın kendisi ayrı testte
        // kanıtlanır (AntiforgeryTests).
        var config = http.RequestServices.GetRequiredService<IConfiguration>();
        if (config.GetValue("Panel:Antiforgery:Enforce", defaultValue: true) is false)
            return await next(context);

        var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            var back = http.Request.Headers.Referer.ToString() is { Length: > 0 } referer
                       && Uri.TryCreate(referer, UriKind.Absolute, out var uri)
                       && uri.Host == http.Request.Host.Host
                ? uri.AbsolutePath
                : "/";
            var flash = Uri.EscapeDataString(PanelFlash.Protect(
                "Oturum güvenlik damgası eskimişti — sayfa yenilendi, lütfen işlemi tekrar deneyin."));
            return Results.Redirect($"{back}?hata={flash}");
        }

        return await next(context);
    }
}

public static class PanelAntiforgeryExtensions
{
    /// <summary>
    /// CSRF koruması: çerçevenin otomatik (çıplak 400'lü) yolu kapatılır, doğrulama
    /// yukarıdaki dostane filtreyle yapılır. Formda &lt;AntiforgeryToken /&gt; olmalı.
    /// </summary>
    public static RouteHandlerBuilder WithPanelAntiforgery(this RouteHandlerBuilder builder)
        => builder.DisableAntiforgery().AddEndpointFilter<AntiforgeryEndpointFilter>();
}
