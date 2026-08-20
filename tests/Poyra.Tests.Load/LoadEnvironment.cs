using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Poyra.Api;
using Poyra.Api.Database;
using Testcontainers.PostgreSql;

namespace Poyra.Tests.Load;

/// <summary>
/// Yük zemini: gerçek Postgres 18 + gerçek Kestrel portunda Poyra.Api.
///
/// <b>TestServer değil Kestrel:</b> bellek içi TestServer soket, TCP ve HTTP ayrıştırma
/// maliyetini atlar. Ölçüm o zaman "uygulama ne kadar hızlı" değil "elden geçirilmiş bir
/// yolda ne kadar hızlı" olurdu.
///
/// <b>poyra_app rolüyle bağlanır:</b> RLS maliyeti ölçüme DAHİLDİR. Sahip rolüyle koşan
/// bir yük testi, üretimde ödenen bedeli görmezden gelirdi.
///
/// <b>Veritabanı:</b> POYRA_LOAD_CS tanımlıysa o kullanılır (ayarlanmış/gerçek sunucuda
/// anlamlı rakam almak için); tanımsızsa Testcontainers ile tek kullanımlık Postgres
/// kalkar. İkincisi mutlak rakam vermez ama GERİLEME ölçmeye yeter — asıl işi budur.
/// </summary>
public sealed class LoadEnvironment : IAsyncDisposable
{
    private const string AdminKey = "load-admin-key";

    private PostgreSqlContainer? _postgres;
    private KestrelFactory? _api;

    public HttpClient Api { get; private set; } = null!;
    public string ApiAdres { get; private set; } = "";
    public string OwnerCs { get; private set; } = "";

    public static async Task<LoadEnvironment> StartAsync()
    {
        var environment = new LoadEnvironment();
        await environment.InitializeAsync();
        return environment;
    }

    private async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("POYRA_LOAD_CS") is { Length: > 0 } external)
        {
            OwnerCs = external;
            Console.WriteLine("Veritabanı: POYRA_LOAD_CS (dışarıdan verildi)");
        }
        else
        {
            _postgres = new PostgreSqlBuilder("postgres:18-alpine")
                .WithUsername("poyra").WithPassword("poyra_pw").WithDatabase("poyra")
                .Build();
            await _postgres.StartAsync();
            OwnerCs = _postgres.GetConnectionString();
            Console.WriteLine("Veritabanı: Testcontainers (tek kullanımlık) — "
                              + "mutlak rakam değil, GERİLEME ölçümü için");
        }

        var appCs = new NpgsqlConnectionStringBuilder(OwnerCs)
        {
            Username = "poyra_app",
            Password = "poyra_app_pw",
        }.ConnectionString;

        await using (var connection = new NpgsqlConnection(OwnerCs))
        {
            await connection.OpenAsync();
            // Yol derleme çıktısına göre çözülür: konsol uygulaması test koşucusundan
            // farklı olarak ÇAĞRILDIĞI dizinde açılır (genelde depo kökü), betik ise
            // csproj tarafından bin/ altına kopyalanır.
            await using var command = new NpgsqlCommand(
                await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, "01-app-role.sql")),
                connection);
            await command.ExecuteNonQueryAsync();
        }

        await DatabaseMigrator.RunAsync(OwnerCs);

        var port = FreePort();
        ApiAdres = $"http://127.0.0.1:{port}";

        _api = new KestrelFactory(port, builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", appCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Platform:AdminKey", AdminKey);
            builder.UseSetting("Poyra:PublicBaseUrl", ApiAdres);
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Poyra:JwtKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            builder.UseSetting("Poyra:VaultKey", Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
        });
        _api.Start();

        Api = new HttpClient
        {
            BaseAddress = new Uri(ApiAdres),
            // Yük altında varsayılan 100 saniyelik zaman aşımı, tıkanmayı "yavaşlık" gibi
            // gösterir: istekler birikir ve rapor sonunda hiç hata görünmez.
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public sealed record Tenant(Guid TenantId, string ApiKey);

    /// <summary>Yük senaryosunun üstünde koşacağı işyeri + MockBank hesabı.</summary>
    public async Task<Tenant> SeedTenantAsync(string label)
    {
        var tenant = await PostAsync<Tenant>("/v1/tenants",
            new { name = label, slug = $"yuk-{Guid.NewGuid():N}"[..14] },
            ("X-Platform-Key", AdminKey));

        await PostAsync<object>("/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Yük POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        return tenant;
    }

    public async Task<T> PostAsync<T>(string path, object body, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);

        using var response = await Api.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"{path} → {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        Api?.Dispose();
        _api?.Dispose();
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }
}

/// <summary>
/// WebApplicationFactory varsayılan olarak TestServer kurar (soketi yok); bu fabrika onun
/// yerine gerçek Kestrel'i bağlar.
///
/// <b>İki host açılır</b> (E2E'deki eşdeğeriyle aynı): WebApplicationFactory dönen host'un
/// sunucusunu TestServer'a cast eder, yani sözleşmeyi bozmadan tek host'la çalışılamaz.
/// Bedeli ölçümde görünür — iki Hangfire sunucusu (2×4 worker) ve iki bağlantı havuzu
/// ölçülen işle aynı süreçte yarışır. Bu sapma TEK YÖNLÜDÜR: Poyra'yı olduğundan YAVAŞ
/// gösterir, hızlı değil. Gerileme ölçümü için kabul edilebilir, rapora da yazılır.
/// (Program.cs'e yalnız yük testi için Hangfire anahtarı eklemek üretim koduna test
/// kaygısı sızdırmak olurdu; ölçüm aracı ölçtüğü şeyi değiştirmemeli.)
/// </summary>
internal sealed class KestrelFactory(int port, Action<IWebHostBuilder> configure)
    : WebApplicationFactory<ApiEntryPoint>
{
    private IHost? _kestrel;

    protected override void ConfigureWebHost(IWebHostBuilder builder) => configure(builder);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var testHost = builder.Build();

        builder.ConfigureWebHost(web => web.UseKestrel().UseUrls($"http://127.0.0.1:{port}"));
        _kestrel = builder.Build();
        _kestrel.Start();

        testHost.Start();
        return testHost;
    }

    /// <summary>WebApplicationFactory tembeldir: bu çağrı olmadan host hiç açılmaz.</summary>
    public void Start() => _ = Services;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _kestrel?.Dispose();
        base.Dispose(disposing);
    }
}
