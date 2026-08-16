using System.Globalization;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Poyra.Modules.Compliance;
using Poyra.Modules.Connectors;
using Poyra.Modules.Customers;
using Poyra.Modules.Field;
using Poyra.Modules.Ledger;
using Poyra.Modules.Disputes;
using Poyra.Modules.Installments;
using Poyra.Modules.Payments;
using Poyra.Modules.Recon;
using Poyra.Modules.Risk;
using Poyra.Modules.Routing;
using Poyra.Modules.PaymentLinks;
using Poyra.Modules.Subscriptions;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Vault;
using Poyra.Modules.Webhooks;
using Poyra.Panel.Components;
using Poyra.Panel.Security;
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

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OneTimeSecretStash>();

var connectionString = builder.Configuration.GetConnectionString("Poyra");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:Poyra tanımlı değil.");

builder.Services.AddSingleton<PostgresNotificationListener>(sp =>
    new PostgresNotificationListener(connectionString,
        sp.GetRequiredService<ILogger<PostgresNotificationListener>>()));
builder.Services.AddSingleton<INotificationStream>(sp => sp.GetRequiredService<PostgresNotificationListener>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PostgresNotificationListener>());
builder.Services.AddSingleton<INotificationPublisher>(sp =>
    new PostgresNotificationPublisher(connectionString,
        sp.GetRequiredService<ILogger<PostgresNotificationPublisher>>()));

builder.Services.AddModuleDbContext<TenancyDbContext>(connectionString, TenancyDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<ConnectorsDbContext>(connectionString, ConnectorsDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<RoutingDbContext>(connectionString, RoutingDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<PaymentsDbContext>(connectionString, PaymentsDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<WebhooksDbContext>(connectionString, WebhooksDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<InstallmentsDbContext>(connectionString, InstallmentsDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<ReconDbContext>(connectionString, ReconDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<VaultDbContext>(connectionString, VaultDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<SubscriptionsDbContext>(
    connectionString, SubscriptionsDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<PaymentLinksDbContext>(
    connectionString, PaymentLinksDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<DisputesDbContext>(
    connectionString, DisputesDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<RiskDbContext>(
    connectionString, RiskDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<ComplianceDbContext>(
    connectionString, ComplianceDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<CustomersDbContext>(
    connectionString, CustomersDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<FieldDbContext>(
    connectionString, FieldDbContext.MigrationsHistoryTable);
builder.Services.AddModuleDbContext<LedgerDbContext>(
    connectionString, LedgerDbContext.MigrationsHistoryTable);

var hangfireCs = builder.Configuration.GetConnectionString("PoyraMigrations");
if (string.IsNullOrWhiteSpace(hangfireCs)) hangfireCs = connectionString;
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(hangfireCs)));

TenancyModule.Add(builder.Services);
ConnectorsModule.Add(builder.Services);
RoutingModule.Add(builder.Services);
WebhooksModule.Add(builder.Services);
InstallmentsModule.Add(builder.Services);
VaultModule.Add(builder.Services);
PaymentsModule.Add(builder.Services);
ReconModule.Add(builder.Services);
SubscriptionsModule.Add(builder.Services);
PaymentLinksModule.Add(builder.Services);
DisputesModule.Add(builder.Services);
RiskModule.Add(builder.Services);
ComplianceModule.Add(builder.Services);
CustomersModule.Add(builder.Services);
FieldModule.Add(builder.Services);
LedgerModule.Add(builder.Services);
builder.Services.AddPoyraCqrs(
    TenancyModule.Assembly, ConnectorsModule.Assembly, RoutingModule.Assembly,
    WebhooksModule.Assembly, InstallmentsModule.Assembly, VaultModule.Assembly,
    PaymentsModule.Assembly, ReconModule.Assembly, SubscriptionsModule.Assembly,
    PaymentLinksModule.Assembly, DisputesModule.Assembly, RiskModule.Assembly,
    ComplianceModule.Assembly, CustomersModule.Assembly, FieldModule.Assembly, LedgerModule.Assembly);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/giris";
        options.LogoutPath = "/cikis";
        options.AccessDeniedPath = "/giris";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "poyra_panel";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<PanelPrincipalBinder>();

builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(options =>
    options.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(
        System.Text.Unicode.UnicodeRanges.All));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

StartupSecrets.EnsureOrThrow(app.Configuration, app.Environment,
    message => app.Logger.LogWarning("{Problems}", message), "CredentialKey", "JwtKey", "VaultKey");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/hata", createScopeForErrors: true);
    app.UseHsts();
}

app.MapStaticAssets();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseMiddleware<PanelTenantMiddleware>();

PanelFlash.Initialize(app.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapPanelAuthEndpoints();
app.MapPanelTotpEndpoints();
app.MapPanelActionEndpoints();

app.Run();

namespace Poyra.Panel
{
    public sealed class PanelEntryPoint;
}
