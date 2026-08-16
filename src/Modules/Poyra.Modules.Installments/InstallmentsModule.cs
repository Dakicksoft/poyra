using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Installments.Contracts;
using Poyra.Modules.Installments.Infrastructure;

namespace Poyra.Modules.Installments;

public sealed class InstallmentsModule
{
    public static readonly Assembly Assembly = typeof(InstallmentsModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services
            .AddScoped<IInstallmentPricing, InstallmentPricing>()
            .AddScoped<IBinLookup, BinLookup>();
}
