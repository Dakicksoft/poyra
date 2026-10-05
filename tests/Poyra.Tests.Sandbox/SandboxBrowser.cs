using System.Net;
using System.Text;
using Microsoft.Playwright;
using Poyra.Connectors.Abstractions;
using Xunit;

namespace Poyra.Tests.Sandbox;

/// <summary>Sağlayıcının tarayıcıyı dönüş adresine gönderdiği istek: sorgu dizesi + varsa form gövdesi.</summary>
public sealed record BrowserReturn(string Url, IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// Gerçek tarayıcı (headless Chromium): 3D Secure ve hosted ödeme sayfaları JS'li ve çok
/// adımlı, HTTP istemcisiyle taklit etmek kırılgan olurdu. Müşterinin yapacağını yapar —
/// kartı girer, banka simülatöründe doğrulama kodunu yazar — ve sağlayıcının dönüş
/// adresine yaptığı yönlendirmeyi YAKALAR. Dönüş adresi gerçek bir sunucu değildir:
/// <c>poyra-sandbox.test</c> isteği tarayıcıda kesilir, içeriği kaydedilir.
/// </summary>
public sealed class SandboxBrowser : IAsyncLifetime
{
    public const string ReturnHost = "https://poyra-sandbox.test";

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync()
    {
        // Kimlik yoksa testler zaten atlanır; tarayıcı indirmek boşuna olurdu.
        if (!LidioSandbox.IsConfigured)
            return;

        var code = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (code != 0)
            throw new InvalidOperationException($"Playwright chromium kurulumu başarısız (çıkış {code}).");

        _playwright = await Playwright.CreateAsync();
        // Varsayılan GÖRÜNÜR pencere: Garanti'nin test 3D motoru (gt3dengine) headless
        // tarayıcıyı "güvenlik politikası" sayfasıyla reddediyor (Eki 2026'da gözlendi).
        // Tespiti atlatmak (sahte user-agent vb.) yerine gerçek bir tarayıcı penceresi açılır;
        // engellemeyen bir ortamda POYRA_SANDBOX_HEADLESS=1 ile headless koşulabilir.
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("POYRA_SANDBOX_HEADLESS") == "1",
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    public static string NewReturnUrl() => $"{ReturnHost}/return/{Guid.NewGuid():N}";

    /// <summary>
    /// 3DS'li direct: konnektörün ürettiği otomatik gönderimli formu açar, Garanti 3D
    /// simülatöründe kodu girer (ya da işlemi iptal eder) ve dönüşü yakalar.
    /// </summary>
    public Task<BrowserReturn> CompleteThreeDsAsync(HostedPaymentForm form, string? otp, Action<string>? log = null)
        => RunAsync(log, async page =>
        {
            await page.SetContentAsync(AutoSubmitForm(form));
            await PassGarantiThreeDsAsync(page, otp);
        });

    /// <summary>
    /// Lidio hosted ödeme sayfası: kartı Lidio'nun sayfasında girer, 3D'yi geçer, dönüşü yakalar.
    /// Kart verisi Poyra sunucusuna hiç uğramaz — müşteri tarayıcısı doğrudan Lidio'ya yazar.
    /// </summary>
    public Task<BrowserReturn> CompleteHostedPageAsync(
        string redirectUrl, CardData card, string? otp, Action<string>? log = null)
        => RunAsync(log, async page =>
        {
            await page.GotoAsync(redirectUrl);
            await FillHostedCardAsync(page, card);
            await PassGarantiThreeDsAsync(page, otp);
        });

    /// <summary>Hosted sayfayı açıp içeriğini döner (ödeme yapmadan) — sayfanın gerçekten açıldığını sınamak için.</summary>
    public async Task<string> ReadPageTextAsync(string url, string waitForText)
    {
        var context = await Browser.NewContextAsync();
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(url);
            // Kart listesi sayfa açıldıktan sonra yüklenir.
            try
            {
                await page.GetByText(waitForText).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            }
            catch (TimeoutException)
            {
                // Metin hiç gelmediyse çağıran test sayfanın son hâlini görüp karar versin.
            }

            return await page.InnerTextAsync("body");
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    private IBrowser Browser
        => _browser ?? throw new InvalidOperationException("Tarayıcı başlatılmadı (sandbox kimliği yok).");

    private async Task<BrowserReturn> RunAsync(Action<string>? log, Func<IPage, Task> drive)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions { Locale = "tr-TR" });
        try
        {
            var returned = new TaskCompletionSource<BrowserReturn>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Dönüş iki yoldan gelir: form/JS gezinmesi (Route keser) ya da sağlayıcının 302
            // yönlendirmesi — Playwright yönlendirme sonrası isteği Route'a VERMEZ, tarayıcı
            // gerçek ağa çıkar. İstek olayı ikisinde de tetiklenir; asıl kayıt oradan alınır.
            context.Request += (_, request) =>
            {
                if (request.Url.StartsWith(ReturnHost, StringComparison.Ordinal))
                    returned.TrySetResult(new BrowserReturn(request.Url, ReadFields(request)));
            };
            await context.RouteAsync($"{ReturnHost}/**", async route =>
            {
                var request = route.Request;
                returned.TrySetResult(new BrowserReturn(request.Url, ReadFields(request)));
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "text/html",
                    Body = "<html><body>poyra-sandbox: dönüş alındı</body></html>",
                });
            });

            var page = await context.NewPageAsync();
            // Başarısızlıkta hangi sayfada kalındığı görülsün: gezinmeler ve belge yanıtları.
            page.FrameNavigated += (_, frame) => log?.Invoke($"→ {frame.Url}");
            page.Response += (_, response) =>
            {
                if (response.Request.ResourceType == "document")
                    log?.Invoke($"  {response.Request.Method} {response.Status} {response.Url}");
            };
            page.SetDefaultTimeout(60_000);
            page.SetDefaultNavigationTimeout(60_000);

            var deadline = Task.Delay(TimeSpan.FromMinutes(3));
            var driving = drive(page);
            var finished = await Task.WhenAny(returned.Task, driving, deadline);
            if (finished == driving)
            {
                if (driving.IsFaulted && !returned.Task.IsCompleted)
                {
                    var failedOn = await SafeBodyAsync(page);
                    throw new InvalidOperationException(
                        $"Tarayıcı sürüşü başarısız. Son sayfa: {page.Url}\n{failedOn}", driving.Exception!.InnerException);
                }

                // Son tıklama döndü; yönlendirme zinciri (banka → Lidio → dönüş) hâlâ sürüyor olabilir.
                await Task.WhenAny(returned.Task, deadline);
            }

            if (!returned.Task.IsCompleted)
            {
                var body = await SafeBodyAsync(page);
                throw new TimeoutException($"Sağlayıcı dönüş adresine yönlendirmedi. Son sayfa: {page.Url}\n{body}");
            }

            return await returned.Task;
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    private static async Task PassGarantiThreeDsAsync(IPage page, string? otp)
    {
        // Lidio önce kendi geçiş sayfasını, ardından bankanın ACS simülatörünü açar. Hosted
        // sayfada ACS, Lidio'nun son adım sayfasındaki bir iframe'in İÇİNDE açılır.
        var acs = await FindFrameAsync(page, "#sixDigitNumber", TimeSpan.FromSeconds(90));

        if (otp is null)
        {
            // "İşlemi İptal Et" tarayıcı onay penceresi açabilir; müşteri onaylar.
            page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
            await acs.Locator("#cancel").ClickAsync();
            return;
        }

        // Devam düğmesi tuş olaylarıyla etkinleşir; Fill (tek seferde değer atama) yetmez.
        await acs.Locator("#sixDigitNumber").PressSequentiallyAsync(otp, new LocatorPressSequentiallyOptions { Delay = 50 });
        await acs.Locator("#js-verification-method-btn").ClickAsync();
    }

    /// <summary>Seçiciyi ana sayfada ve bütün iframe'lerde arar; gezinmeler sürerken de çalışır.</summary>
    private static async Task<IFrame> FindFrameAsync(IPage page, string selector, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var frame in page.Frames)
            {
                try
                {
                    if (await frame.Locator(selector).CountAsync() > 0)
                        return frame;
                }
                catch (PlaywrightException)
                {
                    // Çerçeve tam o anda gezindi ya da kapandı — bir sonraki turda yeniden bakılır.
                }
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"'{selector}' hiçbir çerçevede bulunamadı ({timeout.TotalSeconds:0} sn).");
    }

    private static async Task FillHostedCardAsync(IPage page, CardData card)
    {
        await page.Locator("#txtCardNo").WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        if (card.HolderName is { } holder)
            await page.Locator("#txtCardName").FillAsync(holder);
        // Kart numarası alanı maskeli giriş yapar; tuş tuş yazılmazsa BIN sorgusu tetiklenmez.
        await page.Locator("#txtCardNo").PressSequentiallyAsync(card.Pan, new LocatorPressSequentiallyOptions { Delay = 20 });
        await page.Locator("#cmbLastMonth").SelectOptionAsync(card.ExpiryMonth.ToString());
        await page.Locator("#cmbLastYear").SelectOptionAsync(card.ExpiryYear.ToString());
        await page.Locator("#txtCVV").FillAsync(card.Cvv ?? string.Empty);

        await page.Locator("#btnNext").ClickAsync();
    }

    private static Dictionary<string, string> ReadFields(IRequest request)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var query = new Uri(request.Url).Query.TrimStart('?');
        foreach (var (key, value) in ParsePairs(query))
            fields[key] = value;

        if (request.Method == "POST" && request.PostData is { Length: > 0 } posted)
            foreach (var (key, value) in ParsePairs(posted))
                fields.TryAdd(key, value);

        return fields;
    }

    private static IEnumerable<(string Key, string Value)> ParsePairs(string encoded)
        => encoded.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(parts => (WebUtility.UrlDecode(parts[0]), parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : ""));

    private static string AutoSubmitForm(HostedPaymentForm form)
    {
        var html = new StringBuilder();
        html.Append("<html><body><form id=\"f\" method=\"").Append(WebUtility.HtmlEncode(form.Method))
            .Append("\" action=\"").Append(WebUtility.HtmlEncode(form.ActionUrl)).Append("\">");
        foreach (var (name, value) in form.Fields)
            html.Append("<input type=\"hidden\" name=\"").Append(WebUtility.HtmlEncode(name))
                .Append("\" value=\"").Append(WebUtility.HtmlEncode(value)).Append("\"/>");
        html.Append("</form><script>document.getElementById('f').submit();</script></body></html>");
        return html.ToString();
    }

    private static async Task<string> SafeBodyAsync(IPage page)
    {
        try
        {
            var text = await page.InnerTextAsync("body", new PageInnerTextOptions { Timeout = 5_000 });
            return text.Length > 1500 ? text[..1500] : text;
        }
        catch (PlaywrightException)
        {
            return "(sayfa okunamadı)";
        }
    }
}
