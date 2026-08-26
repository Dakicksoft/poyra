using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
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

    /// <summary>
    /// Verilen işi oturum düzeyi advisory lock altında koşturur.
    ///
    /// Her modülün kendi DbContext'i (dolayısıyla kendi bağlantısı) var; tek transaction
    /// hepsini kapsayamaz. Bu yüzden kilit, tohumlama boyunca AÇIK TUTULAN ayrı bir
    /// bağlantıda alınır. Böylece iki API kopyası aynı anda kalksa bile "işyeri var mı?"
    /// kontrolü ile yazma arasına başka kimse giremez.
    /// </summary>
    public static async Task RunLockedAsync(
        string connectionString, Func<Task> work, CancellationToken cancellationToken)
    {
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken);

        await using (var acquire = new NpgsqlCommand(LockSql, lockConnection))
            await acquire.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            await work();
        }
        finally
        {
            // Bağlantı kapanınca oturum kilitleri zaten düşer; bu açık bırakma
            // yalnız niyeti okunur kılıyor.
            await using var release = new NpgsqlCommand(UnlockSql, lockConnection);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private const string LockSql = "SELECT pg_advisory_lock(20260826)";
    private const string UnlockSql = "SELECT pg_advisory_unlock(20260826)";
}
