using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Persistence.Interceptors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Persistence;

public static class PoyraDb
{
    /// <summary>
    /// Çalışma zamanı kaydı: snake_case adlandırma + modül başına migration geçmişi tablosu
    /// + RLS ve audit interceptor'ları. Bağlantı dizesi 'poyra_app' rolünü kullanmalıdır.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(
        this IServiceCollection services, string connectionString, string migrationsHistoryTable)
        where TContext : ModuleDbContext
        => services.AddDbContext<TContext>((sp, options) => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(migrationsHistoryTable))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                sp.GetRequiredService<RlsConnectionInterceptor>(),
                sp.GetRequiredService<AuditSaveChangesInterceptor>()));


    public static DbContextOptions<TContext> BuildOptions<TContext>(
        string connectionString, string migrationsHistoryTable,
        TenantContext? tenant = null, IClock? clock = null)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(migrationsHistoryTable))
            .UseSnakeCaseNamingConvention();

        if (tenant is not null)
            builder.AddInterceptors(new RlsConnectionInterceptor(tenant));
        if (clock is not null)
            builder.AddInterceptors(new AuditSaveChangesInterceptor(clock));

        return builder.Options;
    }
}
