using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Poyra.Modules.Customers;

public sealed class CustomersModule
{
    public static readonly Assembly Assembly = typeof(CustomersModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services) => services;
}
