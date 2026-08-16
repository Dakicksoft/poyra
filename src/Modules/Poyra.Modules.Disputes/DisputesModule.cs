using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Disputes.Infrastructure;

namespace Poyra.Modules.Disputes;

public sealed class DisputesModule
{
    public static readonly Assembly Assembly = typeof(DisputesModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services
            .AddScoped<IDisputeNotifier, DisputeWebhookNotifier>()
            .AddScoped<DisputeDeadlineJob>();
}
