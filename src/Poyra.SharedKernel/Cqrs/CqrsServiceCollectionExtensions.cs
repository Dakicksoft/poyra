using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Poyra.SharedKernel.Cqrs;

public static class CqrsServiceCollectionExtensions
{
    public static IServiceCollection AddPoyraCqrs(this IServiceCollection services, params Assembly[] assemblies)
    {
        services.AddScoped<IDispatcher, Dispatcher>();

        var handlerDefinitions = new[] { typeof(ICommandHandler<,>), typeof(IQueryHandler<,>), typeof(IValidator<>) };

        var registrations =
            from assembly in assemblies
            from type in assembly.GetTypes()
            where type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
            from iface in type.GetInterfaces()
            where iface.IsGenericType && handlerDefinitions.Contains(iface.GetGenericTypeDefinition())
            select (Service: iface, Implementation: type);

        foreach (var (service, implementation) in registrations)
            services.AddScoped(service, implementation);

        return services;
    }
}
