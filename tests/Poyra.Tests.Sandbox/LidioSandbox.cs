using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Lidio;
using Xunit;

namespace Poyra.Tests.Sandbox;

/// <summary>
/// Lidio test ortamı (test.lidio.com) bağlantısı. Kimlik bilgileri <c>Lidio</c> bölümünden
/// okunur — user-secrets ya da ortam değişkeni (<c>Lidio__MerchantCode</c> …):
/// <code>
/// "Lidio": { "MerchantCode": "…", "ApiKey": "…", "MerchantKey": "…", "ApiPassword": "…" }
/// </code>
/// <c>MerchantKey</c>/<c>ApiPassword</c> verilmezse API anahtarından çözülür (anahtar
/// <c>base64(MerchantKey:ApiPassword)</c> biçimindedir).
/// </summary>
public static class LidioSandbox
{
    public const string DefaultGatewayBase = "https://test.lidio.com/api";

    private static readonly Lazy<IConfigurationSection> Section = new(() =>
        new ConfigurationBuilder()
            .AddUserSecrets(typeof(LidioSandbox).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build()
            .GetSection("Lidio"));

    public static bool IsConfigured
        => !string.IsNullOrWhiteSpace(Section.Value["MerchantCode"])
           && !string.IsNullOrWhiteSpace(Section.Value["ApiKey"]);

    public const string SkipReason =
        "Lidio sandbox kimliği yok (user-secrets ya da Lidio__* ortam değişkenleri) — bkz. tests/Poyra.Tests.Sandbox/README.md";

    public static ConnectorCredentials Credentials => new(new Dictionary<string, string>
    {
        ["gateway_base"] = Section.Value["GatewayBase"] is { Length: > 0 } custom ? custom : DefaultGatewayBase,
        ["merchant_code"] = Section.Value["MerchantCode"]!,
        ["api_key"] = Section.Value["ApiKey"]!,
    });

    public static string MerchantKey
        => Section.Value["MerchantKey"] is { Length: > 0 } key ? key : LidioMessages.SplitApiKey(Section.Value["ApiKey"]!).MerchantKey;

    public static string ApiPassword
        => Section.Value["ApiPassword"] is { Length: > 0 } password ? password : LidioMessages.SplitApiKey(Section.Value["ApiKey"]!).ApiPassword;

    /// <summary>Konnektör ve istemci üretimdeki gibi IHttpClientFactory üzerinden kurulur.</summary>
    public static IHttpClientFactory HttpClientFactory { get; } = BuildFactory();

    public static LidioConnector Connector() => new(HttpClientFactory);

    public static LidioClient Client() => new(HttpClientFactory);

    /// <summary>
    /// Benzersiz Poyra deneme kimliği — üretimdeki gibi <c>att_</c> + 32 hex (36 karakter).
    /// Konnektör bunu Lidio'nun 20 karakter sınırına kendisi indirmeli; testler bunu da sınar.
    /// </summary>
    public static string NewAttemptId() => "att_" + Guid.CreateVersion7().ToString("N");

    /// <summary>İstemci testleri için Lidio kurallarına zaten uyan kısa sipariş no.</summary>
    public static string NewOrderId(string prefix)
        => prefix + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
           + Random.Shared.Next(100, 999).ToString(CultureInfo.InvariantCulture);

    public static string NewCustomerId() => NewOrderId("cus");

    private static IHttpClientFactory BuildFactory()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(LidioConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }
}

/// <summary>Lidio test kartları (developer.lidio.com → Test Card Numbers).</summary>
public static class LidioTestCards
{
    /// <summary>Garanti BBVA kredi kartı; 3D doğrulama kodu <see cref="GarantiOtp"/>.</summary>
    public static readonly CardData Garanti = new("5549602257210013", 2, 2030, "POYRA TEST", "689");

    public const string GarantiOtp = "147852";

    /// <summary>
    /// Garanti BBVA banka kartı. Belge 3D kodunu 123456 der; simülatör onu "hatalı şifre"
    /// sayıyor, kredi kartının kodu (147852) geçiyor (Eki 2026'da gözlendi).
    /// </summary>
    public static readonly CardData GarantiDebit = new("5170414464004676", 3, 2028, "POYRA TEST", "813");

    /// <summary>Yapı Kredi kartı — saklı kart testlerinde ikinci kart olarak.</summary>
    public static readonly CardData YapiKredi = new("4506344222971809", 5, 2029, "POYRA TEST", "000");
}

/// <summary>Kimlik yoksa testi atlar; <c>dotnet test</c> kimliksiz ortamda kırmızı vermez.</summary>
public sealed class LidioSandboxFactAttribute : FactAttribute
{
    public LidioSandboxFactAttribute()
    {
        if (!LidioSandbox.IsConfigured)
            Skip = LidioSandbox.SkipReason;
    }
}

/// <summary>Bütün Lidio sandbox testleri tek koleksiyonda, SIRAYLA koşar — test hesabı ortaktır.</summary>
[CollectionDefinition(Name)]
public sealed class LidioSandboxCollection : ICollectionFixture<SandboxBrowser>
{
    public const string Name = "lidio-sandbox";
}
