using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Subscriptions.Infrastructure;

namespace Poyra.Modules.Subscriptions;

public sealed class SubscriptionsModule
{
    public static readonly Assembly Assembly = typeof(SubscriptionsModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services
            .AddScoped<Contracts.ICustomerSubscriptionSource, Infrastructure.CustomerSubscriptionSource>()
            .AddScoped<SubscriptionBiller>()
            .AddScoped<IWebhookPublisher, SubscriptionEventPublisher>()
            .AddScoped<SubscriptionBillingJob>()
            .AddScoped<DunningRetryJob>();
}
