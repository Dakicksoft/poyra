// Poyra geliştirme orkestrasyonu (.NET Aspire).
//
// Tek komutla ayağa kalkan kurulum: Postgres 18 + API + Panel + Checkout.
//   dotnet run --project src/Poyra.AppHost
//
// ÖNEMLİ — iki rollü veritabanı (İlke 4, iki katmanlı işyeri izolasyonu):
//   * poyra      → tablo sahibi; YALNIZ migration koşar.
//   * poyra_app  → uygulamanın bağlandığı rol; NOBYPASSRLS, RLS'e tabidir.
// poyra_app rolü docker/initdb betiklerinden doğar — aynı betikler docker compose
// kurulumunda ve Testcontainers fikstüründe de koşar, üç ortam tek kaynaktan beslenir.

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// --- Veritabanı --------------------------------------------------------------
// Parolalar geliştirme varsayılanıdır; üretim kurulumu Aspire ile değil
// docker-compose.prod.yml + .env ile yapılır (bkz. README "Üretim kurulumu").
var pgUser = builder.AddParameter("pg-kullanici", "poyra");
var pgSifre = builder.AddParameter("pg-sifre", "poyra_pw", secret: true);

var postgres = builder
    .AddPostgres("postgres", pgUser, pgSifre, port: 5442)
    .WithImage("postgres", "18-alpine")
    // Sabit port: depodaki dokümanlar, appsettings.Development.json ve dotnet-ef
    // komutları 5442'yi bekler. Bu yüzden Aspire ile `docker compose up` AYNI ANDA
    // çalıştırılmaz — ikisi de bu portu ister.
    .WithDataVolume("poyra-aspire-pgdata")
    // poyra_app rolü + varsayılan yetkiler burada doğar.
    .WithInitFiles("../../docker/initdb");

// POSTGRES_DB, kullanıcı adına eşitlenir (postgres imajının davranışı) — veritabanı
// adı da "poyra" olur; initdb betikleri bu veritabanında koşar.
var veritabani = postgres.AddDatabase("poyra");

// Uygulama rolünün bağlantısı: aynı sunucu, farklı rol. Aspire'ın ürettiği bağlantı
// dizesi sahip roldür; uygulamalar ona ASLA bağlanmaz.
var uygulamaBaglantisi = ReferenceExpression.Create(
    $"Host={postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Host)};" +
    $"Port={postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Port)};" +
    $"Database=poyra;Username=poyra_app;Password=poyra_app_pw");

// --- Uygulamalar -------------------------------------------------------------
// Geliştirmede şemayı API açılışta uygular (Database:AutoMigrate); Panel ve Checkout
// bu yüzden API'nin hazır olmasını bekler — boş şemaya bağlanıp patlamazlar.
var api = builder.AddProject<Projects.Poyra_Api>("api")
    .WithEnvironment("ConnectionStrings__Poyra", uygulamaBaglantisi)
    .WithEnvironment("ConnectionStrings__PoyraMigrations", veritabani)
    .WithHttpHealthCheck("/health/ready")
    .WaitFor(veritabani);

var panel = builder.AddProject<Projects.Poyra_Panel>("panel")
    .WithEnvironment("ConnectionStrings__Poyra", uygulamaBaglantisi)
    .WithEnvironment("ConnectionStrings__PoyraMigrations", veritabani)
    .WaitFor(api);

var checkout = builder.AddProject<Projects.Poyra_Checkout>("checkout")
    .WithEnvironment("ConnectionStrings__Poyra", uygulamaBaglantisi)
    .WithEnvironment("ConnectionStrings__PoyraMigrations", veritabani)
    .WaitFor(api);

// --- Karşılıklı adresler -----------------------------------------------------
// Üç host birbirinin mutlak adresini bilir (ödeme linki, checkout yönlendirmesi,
// panelin API adresi). Adresler Aspire'ın uç noktalarından gelir; elle yazılan
// localhost portu kalmaz.
api.WithEnvironment("Poyra__PanelBaseUrl", panel.GetEndpoint("http"))
   .WithEnvironment("Poyra__CheckoutBaseUrl", checkout.GetEndpoint("http"));

panel.WithEnvironment("Poyra__PublicBaseUrl", api.GetEndpoint("http"))
     .WithEnvironment("Poyra__CheckoutBaseUrl", checkout.GetEndpoint("http"));

checkout.WithEnvironment("Poyra__PublicBaseUrl", api.GetEndpoint("http"))
        .WithEnvironment("Poyra__PanelBaseUrl", panel.GetEndpoint("http"));

builder.Build().Run();
