using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Poyra.Field.Core;

namespace Poyra.Field.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Kuyruk cihazın KENDİ verisidir: uygulama silinene kadar yaşar.
        // FileSystem.AppDataDirectory yedeklenmez ve kullanıcı temizleyemez —
        // "önbelleği temizle" bir günlük tahsilatı silmemelidir.
        var dbPath = Path.Combine(FileSystem.AppDataDirectory, "poyra-saha.db");

        builder.Services.AddSingleton(_ => FieldQueueDbContext.Open(dbPath));
        builder.Services.AddSingleton<FieldQueue>();
        builder.Services.AddSingleton(sp => new FieldSyncClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("poyra"),
            sp.GetRequiredService<FieldQueue>(),
            () => DateTimeOffset.Now));

        // Adres ve anahtar AYAR EKRANINDAN değişebilir; istemci her istekte güncel
        // değeri okumalı — açılışta bir kez okunsaydı ayar değişince yeniden başlatma
        // gerekirdi ve temsilci nedenini bilemezdi.
        builder.Services.AddHttpClient("poyra")
            .ConfigureHttpClient(client =>
            {
                // YER TUTUCU adres — gerçek sunucu SettingsHandler'da yazılır.
                // Boş bırakılamaz: HttpClient göreli adresi handler ÇALIŞMADAN ÖNCE
                // doğrular ve "invalid request uri" ile patlar.
                client.BaseAddress = new Uri(SettingsHandler.Placeholder);
                // Saha şebekesi yavaştır; kısa zaman aşımı kuyruğu boşuna "ulaşılamadı"ya düşürür
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler(() => new SettingsHandler());

        builder.Services.AddSingleton<CollectionPage>();
        builder.Services.AddSingleton<SettingsPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}

/// <summary>
/// Cihaz yapılandırması — <b>kalıcıdır</b>. Temsilci kodunu her açılışta yeniden
/// yazmak zorunda kalsaydı uygulama sahada kullanılamazdı.
///
/// <c>Preferences</c> uygulamanın özel alanındadır; kullanıcı "önbelleği temizle"
/// dediğinde silinmez.
///
/// TODO(cihaz): API anahtarı burada GEÇİCİ olarak duruyor — işyerinin tam yetkili
/// makine kimliğidir ve cihaz kaybolursa tüm işyeri açığa çıkar. Kalıcı çözüm:
/// temsilcinin kullanıcı girişiyle cihaza özel, dar yetkili ve iptal edilebilir
/// bir belirteç alması (kayıt/enrolment akışı).
/// </summary>
public static class AppSettings
{
    private const string BaseUrlKey = "poyra.baseUrl";
    private const string ApiKeyKey = "poyra.apiKey";
    private const string AgentCodeKey = "poyra.agentCode";
    private const string DeviceIdKey = "poyra.deviceId";

    /// <summary>Android emülatöründe ana makine 10.0.2.2 adresinden görünür.</summary>
    public const string DefaultBaseUrl = "http://10.0.2.2:5080";

    public static string ApiBaseUrl
    {
        get => Preferences.Get(BaseUrlKey, DefaultBaseUrl);
        set => Preferences.Set(BaseUrlKey, value);
    }

    public static string ApiKey
    {
        get => Preferences.Get(ApiKeyKey, string.Empty);
        set => Preferences.Set(ApiKeyKey, value);
    }

    public static string AgentCode
    {
        get => Preferences.Get(AgentCodeKey, string.Empty);
        set => Preferences.Set(AgentCodeKey, value);
    }

    /// <summary>
    /// Cihaz kimliği. İlk açılışta ÜRETİLİR ve bir daha DEĞİŞMEZ: her açılışta yeniden
    /// üretilseydi sunucu her seferinde "başka cihaz" görüp senkronu reddederdi.
    /// </summary>
    public static string DeviceId
    {
        get
        {
            var existing = Preferences.Get(DeviceIdKey, string.Empty);
            if (existing.Length > 0)
                return existing;

            var generated = $"{DeviceInfo.Current.Model}-{Guid.CreateVersion7().ToString("N")[..8]}"
                .Replace(' ', '-');
            Preferences.Set(DeviceIdKey, generated);
            return generated;
        }
    }

    public static bool IsConfigured
        => AgentCode.Length > 0 && ApiKey.Length > 0;
}

public sealed class App : Application
{
    private readonly CollectionPage _page;

    public App(CollectionPage page) => _page = page;

    protected override Window CreateWindow(IActivationState? activationState)
        => new(new NavigationPage(_page) { Title = "Poyra Saha" });
}
