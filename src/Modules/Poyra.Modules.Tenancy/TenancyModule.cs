using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.Modules.Tenancy.Domain;
using Poyra.Modules.Tenancy.Infrastructure;
using Poyra.SharedKernel.Messaging;

namespace Poyra.Modules.Tenancy;

public sealed class TenancyModule
{
    public static readonly Assembly Assembly = typeof(TenancyModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services
            .AddSingleton<JwtTokens>()
            .AddScoped<IPasswordHasher<User>, PasswordHasher<User>>() // PBKDF2, ASP.NET Identity varsayılanı
            .AddScoped<ITenantDirectory, TenantDirectory>()
            .AddScoped<ITenantBrandingSource, Features.Branding.TenantBrandingSource>()
            .AddScoped<IEmailQueue, EmailQueue>()
            .AddScoped<EmailDispatchJob>()
            .AddScoped<IEmailTransport>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return string.IsNullOrWhiteSpace(configuration["Poyra:Smtp:Host"])
                    ? new LoggingEmailTransport(sp.GetRequiredService<ILogger<LoggingEmailTransport>>())
                    : new SmtpEmailTransport(configuration);
            })
            .AddScoped<ISmsQueue, SmsQueue>()
            .AddScoped<SmsDispatchJob>()
            .AddScoped<ISmsTransport>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return configuration["Sms:Provider"]?.ToLowerInvariant() switch
                {
                    "netgsm" => new NetgsmSmsTransport(
                        sp.GetRequiredService<IHttpClientFactory>(), configuration),
                    "iletimerkezi" => new IletiMerkeziSmsTransport(
                        sp.GetRequiredService<IHttpClientFactory>(), configuration),
                    _ => new LoggingSmsTransport(sp.GetRequiredService<ILogger<LoggingSmsTransport>>()),
                };
            });
}
