using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Risk.Infrastructure;

namespace Poyra.Modules.Risk;

public sealed class RiskModule
{
    public static readonly Assembly Assembly = typeof(RiskModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services.AddScoped<IRiskGate, RiskEngine>();
}
