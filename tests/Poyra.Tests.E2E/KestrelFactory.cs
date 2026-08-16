using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Poyra.Tests.E2E;

/// <summary>
/// WebApplicationFactory varsayılan olarak TestServer kurar: bellek içi, soketi yok.
/// Gerçek bir tarayıcı oraya BAĞLANAMAZ. Bu fabrika aynı yapılandırmayla ikinci bir
/// host'u gerçek Kestrel ile açar; Playwright ona bağlanır.
///
/// Port dışarıdan verilir çünkü üç uygulama birbirinin adresini AÇILIRKEN bilmek
/// zorunda: checkout'un ödeme callback'i Api'nin adresine gider, panel bağlantı
/// üretirken checkout'un adresini yazar. "Aç, adresi öğren, sonra yapılandır" sırası
/// bu döngüde çalışmaz (bkz. PoyraAppFixture.BosPortAyir).
/// </summary>
public sealed class KestrelFactory<TEntryPoint>(int port, Action<IWebHostBuilder> yapilandir)
    : WebApplicationFactory<TEntryPoint> where TEntryPoint : class
{
    private IHost? _kestrel;

    public string Adres { get; } = $"http://127.0.0.1:{port}";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => yapilandir(builder);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Sözleşme gereği TestServer host'u (WebApplicationFactory bunun dönmesini bekler)
        var testHost = builder.Build();

        // Aynı yapılandırma + gerçek Kestrel
        builder.ConfigureWebHost(web => web.UseKestrel().UseUrls(Adres));
        _kestrel = builder.Build();
        _kestrel.Start();

        testHost.Start();
        return testHost;
    }

    /// <summary>
    /// Host'u şimdi kurar. WebApplicationFactory tembeldir: CreateHost ilk erişimde
    /// çalışır, yani bu çağrı olmadan uygulama hiç açılmaz ve tarayıcı boş porta gider.
    /// </summary>
    public void Baslat() => _ = Services;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _kestrel?.Dispose();
        base.Dispose(disposing);
    }
}
