using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Routing.Infrastructure;

namespace Poyra.Modules.Routing;

public sealed class RoutingModule
{
    public static readonly Assembly Assembly = typeof(RoutingModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services.AddScoped<IRoutingService, RoutingEngine>();
}
