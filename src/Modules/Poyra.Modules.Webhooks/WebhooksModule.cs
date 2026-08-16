using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Webhooks.Contracts;
using Poyra.Modules.Webhooks.Infrastructure;

namespace Poyra.Modules.Webhooks;

public sealed class WebhooksModule
{
    public static readonly Assembly Assembly = typeof(WebhooksModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddHttpClient(WebhookDeliveryJob.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IWebhookFanout, WebhookFanout>();
        services.AddScoped<WebhookDeliveryJob>();
        return services;
    }
}
