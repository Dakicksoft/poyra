using System.Globalization;
using Hangfire;
using Hangfire.PostgreSql;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Poyra.Checkout;
using Poyra.Checkout.Components;
using Poyra.Modules.Connectors;
using Poyra.Modules.Installments;
using Poyra.Modules.PaymentLinks;
using Poyra.Modules.Payments;
using Poyra.Modules.Ledger;
using Poyra.Modules.Recon;
using Poyra.Modules.Routing;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Vault;
using Poyra.Modules.Webhooks;
using Poyra.Persistence;
using Poyra.Persistence.Interceptors;
using Poyra.Persistence.Notifications;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Notifications;
using Poyra.SharedKernel.Security;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

var trCulture = CultureInfo.GetCultureInfo("tr-TR");
CultureInfo.DefaultThreadCurrentCulture = trCulture;
CultureInfo.DefaultThreadCurrentUICulture = trCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<UserContext>();
builder.Services.AddScoped<RlsConnectionInterceptor>();
builder.Services.AddScoped<AuditSaveChangesInterceptor>(sp =>
    new AuditSaveChangesInterceptor(sp.GetRequiredService<IClock>()));
builder.Services.AddSingleton<ICredentialProtector, AesGcmCredentialProtector>();

var connectionString = builder.Configuration.GetConnectionString("Poyra");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:Poyra tanımlı değil.");

builder.Services.AddModuleDbContext<TenancyDbContext>(connectionString, TenancyDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<ConnectorsDbContext>(connectionString, ConnectorsDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<RoutingDbContext>(connectionString, RoutingDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<PaymentsDbContext>(connectionString, PaymentsDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<WebhooksDbContext>(connectionString, WebhooksDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<InstallmentsDbContext>(connectionString, InstallmentsDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<ReconDbContext>(connectionString, ReconDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<LedgerDbContext>(connectionString, LedgerDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<VaultDbContext>(connectionString, VaultDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<PaymentLinksDbContext>(
    connectionString, PaymentLinksDbContext.MigrationsHistoryTable);

var hangfireCs = builder.Configuration.GetConnectionString("PoyraMigrations");
if (string.IsNullOrWhiteSpace(hangfireCs)) hangfireCs = connectionString;
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(hangfireCs)));

builder.Services.AddSingleton<INotificationPublisher>(sp =>
    new PostgresNotificationPublisher(connectionString,
        sp.GetRequiredService<ILogger<PostgresNotificationPublisher>>()));

TenancyModule.Add(builder.Services);
ConnectorsModule.Add(builder.Services);
RoutingModule.Add(builder.Services);
WebhooksModule.Add(builder.Services);
InstallmentsModule.Add(builder.Services);
VaultModule.Add(builder.Services);
PaymentsModule.Add(builder.Services);
LedgerModule.Add(builder.Services);
ReconModule.Add(builder.Services);
PaymentLinksModule.Add(builder.Services);
builder.Services.AddPoyraCqrs(
    TenancyModule.Assembly, ConnectorsModule.Assembly, RoutingModule.Assembly, WebhooksModule.Assembly,
    InstallmentsModule.Assembly, VaultModule.Assembly, PaymentsModule.Assembly, LedgerModule.Assembly, ReconModule.Assembly,
    PaymentLinksModule.Assembly);

builder.Services.AddWebEncoders(options =>
    options.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(
        System.Text.Unicode.UnicodeRanges.All));

builder.Services.AddRazorComponents();


var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("poyra-checkout"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         .AddNpgsql();
        if (otlpEndpoint is not null) t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation();
        if (otlpEndpoint is not null) m.AddOtlpExporter();
    });

var app = builder.Build();

StartupSecrets.EnsureOrThrow(app.Configuration, app.Environment,
    message => app.Logger.LogWarning("{Problems}", message), "CredentialKey");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/hata", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>();
app.MapCheckoutEndpoints();
app.MapBrandingEndpoints();

app.Run();

namespace Poyra.Checkout
{
    public sealed class CheckoutEntryPoint;
}
