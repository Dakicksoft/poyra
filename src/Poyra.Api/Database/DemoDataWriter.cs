using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Tenancy.Features.CreateTenant;
using Poyra.Persistence;
using Poyra.SharedKernel.Cqrs;

namespace Poyra.Api.Database;

/// <summary>
/// Demo satırlarını yazar. Hiçbir metodu veri SİLMEZ; tohumlayıcı zaten yalnız
/// işyeri bulunmayan bir veritabanında çağırır.
/// </summary>
public static class DemoDataWriter
{
    /// <summary>
    /// tenants RLS'siz bir platform tablosudur; işyeri bağlamı kurulmadan sorgulanır
    /// (CreateTenantHandler da slug çakışmasını aynı şekilde kontrol ediyor).
    /// </summary>
    public static async Task<bool> TenantExistsAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TenancyDbContext>();
        return await db.Tenants.AnyAsync(cancellationToken);
    }

    public static async Task WriteAsync(
        IServiceProvider services,
        DemoSeedOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var dispatcher = services.GetRequiredService<IDispatcher>();

        // Mevcut ve doğrulanmış yol: organizasyon + işyeri + varsayılan profil +
        // API anahtarı + parolası hash'lenmiş sahip kullanıcı birlikte kurulur.
        // Elle User üretip parola hash'lemek bu yolu atlamak olurdu.
        var tenant = await dispatcher.Send(
            new CreateTenantCommand(
                options.TenantName,
                options.TenantSlug,
                options.Email,
                options.Password,
                options.OwnerName),
            cancellationToken);

        logger.LogInformation(
            "Demo işyeri kuruldu: {Slug} ({TenantId}).", tenant.Slug, tenant.TenantId);
    }
}
