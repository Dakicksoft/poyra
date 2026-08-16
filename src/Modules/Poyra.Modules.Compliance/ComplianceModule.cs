using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Compliance.Infrastructure;
using Poyra.SharedKernel.Audit;

namespace Poyra.Modules.Compliance;

public sealed class ComplianceModule
{
    public static readonly Assembly Assembly = typeof(ComplianceModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services.AddScoped<IAuditTrail, AuditTrailWriter>();
}
