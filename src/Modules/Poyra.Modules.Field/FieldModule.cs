using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Field.Infrastructure;

namespace Poyra.Modules.Field;

public sealed class FieldModule
{
    public static readonly Assembly Assembly = typeof(FieldModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services.AddScoped<FieldOutcomeJob>();
}
