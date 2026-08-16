namespace Poyra.Field.App;

/// <summary>
/// Her isteğe güncel sunucu adresini ve anahtarı koyar.
///
/// Değerler <b>istek anında</b> okunur, açılışta değil: ayar ekranından adres
/// değiştiren temsilci uygulamayı yeniden başlatmak zorunda kalmamalı — sahada
/// "kapat aç" talimatı, kaybolan bir gün demektir.
/// </summary>
public sealed class SettingsHandler : DelegatingHandler
{
    /// <summary>
    /// HttpClient'a verilen sahte taban adres. Gerçek adres ayarlardan gelir ama
    /// HttpClient göreli adresi handler zincirinden ÖNCE doğruladığı için taban
    /// adresin boş olmaması gerekir — boş bırakıldığında uygulama senkronda çöküyordu.
    /// </summary>
    public const string Placeholder = "http://poyra.invalid/";

    public static Uri Rewrite(Uri requestUri, string configuredBaseUrl)
    {
        var target = new Uri(configuredBaseUrl.TrimEnd('/') + "/");
        return new Uri(target, requestUri.PathAndQuery.TrimStart('/'));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri)
            request.RequestUri = Rewrite(uri, AppSettings.ApiBaseUrl);

        request.Headers.Remove("X-Api-Key");
        if (AppSettings.ApiKey is { Length: > 0 } key)
            request.Headers.Add("X-Api-Key", key);

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Cihaz kurulumu: temsilci kodu, sunucu adresi ve anahtar.
///
/// <b>Neden bir ekran gerekiyor:</b> bu değerler olmadan cihaz senkron edemez.
/// Uygulamaya gömülü sabitler olsaydı her bayi için ayrı APK derlemek gerekirdi.
/// </summary>
public sealed class SettingsPage : ContentPage
{
    private readonly Entry _agentCode = new() { Placeholder = "Temsilci kodu (ör. BAYI-01)" };
    private readonly Entry _baseUrl = new() { Placeholder = "Sunucu adresi", Keyboard = Keyboard.Url };
    private readonly Entry _apiKey = new() { Placeholder = "API anahtarı", IsPassword = true };
    private readonly Label _deviceId = new() { FontSize = 12 };
    private readonly Label _status = new() { FontSize = 14 };

    public SettingsPage()
    {
        Title = "Cihaz kurulumu";
        Padding = 16;

        var save = new Button { Text = "Kaydet", HeightRequest = 52 };
        save.Clicked += async (_, _) => await SaveAsync();

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    new Label { Text = "Temsilci kodu", FontAttributes = FontAttributes.Bold },
                    _agentCode,
                    new Label { Text = "Sunucu adresi", FontAttributes = FontAttributes.Bold },
                    _baseUrl,
                    new Label { Text = "API anahtarı", FontAttributes = FontAttributes.Bold },
                    _apiKey,
                    new Label
                    {
                        // Temsilci "cihaz eşleşmiyor" hatası aldığında yöneticiye
                        // hangi cihazı serbest bırakacağını söyleyebilmeli
                        Text = "Bu cihazın kimliği (yöneticiye bildirin):",
                        FontSize = 12,
                    },
                    _deviceId,
                    save,
                    _status,
                },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _agentCode.Text = AppSettings.AgentCode;
        _baseUrl.Text = AppSettings.ApiBaseUrl;
        _apiKey.Text = AppSettings.ApiKey;
        _deviceId.Text = AppSettings.DeviceId;
        _status.Text = AppSettings.IsConfigured ? "Cihaz kurulu." : "Cihaz henüz kurulmadı.";
    }

    private async Task SaveAsync()
    {
        var code = _agentCode.Text?.Trim() ?? string.Empty;
        var url = _baseUrl.Text?.Trim() ?? string.Empty;

        if (code.Length == 0 || url.Length == 0)
        {
            await DisplayAlertAsync("Eksik bilgi", "Temsilci kodu ve sunucu adresi zorunludur.", "Tamam");
            return;
        }

        AppSettings.AgentCode = code;
        AppSettings.ApiBaseUrl = url;
        AppSettings.ApiKey = _apiKey.Text?.Trim() ?? string.Empty;

        _status.Text = "Kaydedildi.";
        await Navigation.PopAsync();
    }
}
