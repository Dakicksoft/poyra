using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json;
using FastEndpoints;
using FastEndpoints.Swagger;
using Hangfire;
using Hangfire.PostgreSql;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Poyra.Api;
using Poyra.Api.Database;
using Poyra.Api.Infrastructure;
using Poyra.Api.Middleware;
using Poyra.Modules.Compliance;
using Poyra.Modules.Connectors;
using Poyra.Modules.Customers;
using Poyra.Modules.Field;
using Poyra.Modules.Ledger;
using Poyra.Modules.Disputes;
using Poyra.Modules.Installments;
using Poyra.Modules.PaymentLinks;
using Poyra.Modules.Payments;
using Poyra.Modules.Payments.Infrastructure;
using Poyra.Modules.Recon;
using Poyra.Modules.Risk;
using Poyra.Modules.Routing;
using Poyra.Modules.Subscriptions;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Vault;
using Poyra.Modules.Webhooks;
using Poyra.Persistence;
using Poyra.Persistence.Interceptors;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Security;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;
using Scalar.AspNetCore;

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

builder.Services.AddSingleton<Poyra.SharedKernel.Notifications.INotificationPublisher>(sp =>
    new Poyra.Persistence.Notifications.PostgresNotificationPublisher(
        sp.GetRequiredService<IConfiguration>().GetConnectionString("Poyra")!,
        sp.GetRequiredService<ILogger<Poyra.Persistence.Notifications.PostgresNotificationPublisher>>()));

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
builder.Services.AddHangfireServer(o =>
{
    o.WorkerCount = 4;
    o.SchedulePollingInterval = builder.Environment.IsProduction()
        ? TimeSpan.FromSeconds(15)
        : TimeSpan.FromSeconds(1);
});

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

builder.Services.AddFastEndpoints(o => o.Assemblies =
    [TenancyModule.Assembly, ConnectorsModule.Assembly, RoutingModule.Assembly,
     WebhooksModule.Assembly, InstallmentsModule.Assembly, VaultModule.Assembly,
     PaymentsModule.Assembly, ReconModule.Assembly, SubscriptionsModule.Assembly,
     PaymentLinksModule.Assembly, DisputesModule.Assembly, RiskModule.Assembly,
     ComplianceModule.Assembly, CustomersModule.Assembly, FieldModule.Assembly, LedgerModule.Assembly]);

builder.Services.SwaggerDocument(o =>
{
    o.DocumentSettings = s =>
    {
        s.DocumentName = "v1";
        s.Title = "Poyra API";
        s.Version = "v1";
        s.Description = ApiDocs.Overview;

        s.AddAuth("ApiKey", new NSwag.OpenApiSecurityScheme
        {
            Type = NSwag.OpenApiSecuritySchemeType.ApiKey,
            Name = "X-Api-Key",
            In = NSwag.OpenApiSecurityApiKeyLocation.Header,
            Description = "Sunucudan sunucuya çağrılar. İşyerinin tam yetkili makine kimliği — "
                          + "tarayıcıya, mobil uygulamaya veya istemci koduna KOYULMAZ.",
        });

        s.AddAuth("Bearer", new NSwag.OpenApiSecurityScheme
        {
            Type = NSwag.OpenApiSecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "İnsan kullanıcı oturumu (`POST /v1/auth/login`). 10 dakika geçerlidir "
                          + "ve rol sırasına tabidir; API anahtarı ise rol denetiminden muaftır.",
        });

        s.PostProcess = document =>
        {
            document.Security.Add(new NSwag.OpenApiSecurityRequirement
            {
                ["ApiKey"] = [],
            });
        };
    };

    o.EnableJWTBearerAuth = false;

    o.ShortSchemaNames = false;
});

var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("poyra-api"))
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
    message => app.Logger.LogWarning("{Problems}", message), "CredentialKey", "JwtKey", "VaultKey");

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    var migrationCs = app.Configuration.GetConnectionString("PoyraMigrations");
    if (string.IsNullOrWhiteSpace(migrationCs)) migrationCs = connectionString;
    await DatabaseMigrator.RunAsync(migrationCs, app.Logger);
}

if (app.Configuration.GetValue<bool>("Poyra:MigrateOnly"))
{
    app.Logger.LogInformation("MigrateOnly: şema hazır, süreç kapanıyor.");
    return;
}

await DatabaseRoleGuard.EnsureNotPrivilegedAsync(connectionString, app.Environment, app.Logger);

// Demo verisi: yalnız bayrak açıkken ve veritabanında HİÇ işyeri yokken. Kilit,
// birden çok kopya aynı anda kalkarsa yalnız birinin tohumlamasını sağlar.
// Hata çıkarsa açılış sürer — demo verisi uygulamayı düşürmeye değmez.
var demoOptions = app.Configuration.GetSection(DemoSeedOptions.Section).Get<DemoSeedOptions>()
                  ?? new DemoSeedOptions();

if (demoOptions.Enabled)
{
    await using var demoScope = app.Services.CreateAsyncScope();
    await DemoDataWriter.RunLockedAsync(connectionString, async () =>
        await DemoDataSeeder.SeedAsync(
            demoOptions,
            ct => DemoDataWriter.TenantExistsAsync(demoScope.ServiceProvider, ct),
            ct => DemoDataWriter.WriteAsync(demoScope.ServiceProvider, demoOptions, app.Logger, ct),
            app.Logger),
        CancellationToken.None);
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<AuditMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<IdempotencyMiddleware>();

app.UseFastEndpoints(c =>
{
    c.Endpoints.Configurator = ep => ep.AllowAnonymous();
    c.Serializer.Options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    c.Serializer.Options.Converters.Add(new UtcDateTimeOffsetConverter());
});

app.UseSwaggerGen();
app.MapScalarApiReference("/docs", o =>
{
    o.Title = "Poyra API";
    o.OpenApiRoutePattern = "/swagger/{documentName}/swagger.json";
    o.AddDocument("v1");
});

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization =
    [
        new PlatformKeyDashboardFilter(
            app.Configuration["Platform:AdminKey"], app.Environment.IsDevelopment()),
    ],
});

using (var scope = app.Services.CreateScope())
{
    var recurring = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurring.AddOrUpdate<OutboxDispatcher>(
        "payments-outbox-sweep", d => d.DispatchPendingAsync(), "* * * * *");
    recurring.AddOrUpdate<ThreeDsTimeoutJob>(
        "payments-3ds-timeout", j => j.ExpireStaleAsync(), "*/5 * * * *");
    recurring.AddOrUpdate<Poyra.Modules.Connectors.Infrastructure.ConnectorCanaryJob>(
        "connectors-canary", j => j.ProbeAllAsync(), "*/10 * * * *");
    recurring.AddOrUpdate<Poyra.Modules.Recon.Infrastructure.ReconStuckSweepJob>(
        "recon-stuck-sweep", j => j.RequeueStuckAsync(), "*/5 * * * *");
    recurring.AddOrUpdate<Poyra.Modules.Subscriptions.Infrastructure.SubscriptionBillingJob>(
        "subscriptions-billing", j => j.BillAllAsync(), "0 3 * * *");
    recurring.AddOrUpdate<Poyra.Modules.Subscriptions.Infrastructure.DunningRetryJob>(
        "subscriptions-dunning", j => j.RetryAllAsync(), "0 * * * *");
    recurring.AddOrUpdate<Poyra.Modules.Tenancy.Infrastructure.EmailDispatchJob>(
        "email-dispatch", j => j.DispatchPendingAsync(), "* * * * *");
    recurring.AddOrUpdate<Poyra.Modules.Tenancy.Infrastructure.SmsDispatchJob>(
        "sms-dispatch", j => j.DispatchPendingAsync(), "* * * * *");
    recurring.AddOrUpdate<Poyra.Modules.Disputes.Infrastructure.DisputeDeadlineJob>(
        "disputes-deadline-sweep", j => j.SweepAsync(), "0 * * * *");
    recurring.AddOrUpdate<Poyra.Modules.PaymentLinks.Infrastructure.PaymentLinkOutcomeJob>(
        "payment-link-outcome", j => j.ResolveAsync(), "*/2 * * * *");
    recurring.AddOrUpdate<Poyra.Modules.Ledger.Infrastructure.ReceivableProjectionJob>(
        "ledger-receivable-projection", j => j.ProjectAsync(), "*/5 * * * *");
    recurring.AddOrUpdate<Poyra.Modules.Ledger.Infrastructure.OverdueReceivableJob>(
        "ledger-overdue-flag", j => j.FlagAsync(), "0 6 * * *");
    recurring.AddOrUpdate<Poyra.Modules.Field.Infrastructure.FieldOutcomeJob>(
        "field-outcome-resolve", j => j.ResolveAsync(), "*/5 * * * *");
}

app.Run();

namespace Poyra.Api
{
    public sealed class ApiEntryPoint;
}
