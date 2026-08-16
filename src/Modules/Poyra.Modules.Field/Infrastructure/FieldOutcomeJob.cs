using Hangfire;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Field.Contracts;
using Poyra.Modules.Field.Domain;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Field.Infrastructure;

/// <summary>
/// Cihaz "tahsil edildi" diyemez — o hâlde bu durumu
/// kim yazacak? Bu iş yazar.
///
/// Bağlantısı üretilmiş her saha kaydı için ödeme sonucu SUNUCUDAN sorulur ve kayıt
/// <c>succeeded</c>/<c>failed</c>'a taşınır. Temsilcinin telefonu kapalı olsa, uygulama
/// silinse, cihaz kaybolsa bile para durumu doğru oluşur: tahsilatın gerçekliği cihazın
/// hayatta olmasına bağlı olamaz.
///
/// field_collections RLS'lidir → işyeri döngüsü <see cref="ITenantDirectory"/> ile kurulur.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class FieldOutcomeJob(
    FieldDbContext db,
    TenantContext tenant,
    ITenantDirectory tenants,
    IFieldCheckoutLinks links,
    IClock clock)
{
    /// <summary>
    /// Sonucu beklenen kayıt sayısı tavanı. Tavan olmasaydı, uzun süre ödenmemiş
    /// binlerce bağlantı her turda yeniden sorgulanır ve iş hiç bitmezdi.
    /// </summary>
    public const int BatchSize = 200;

    public async Task ResolveAsync()
    {
        foreach (var tenantId in await tenants.GetActiveTenantIdsAsync(default))
        {
            tenant.Set(tenantId);
            await ResolveForTenantAsync();
        }
    }

    private async Task ResolveForTenantAsync()
    {
        var waiting = await db.FieldCollections
            .Where(c => c.Status == FieldCollectionStatus.LinkIssued && c.PaymentLinkId != null)
            .OrderBy(c => c.OccurredAtServer)
            .Take(BatchSize)
            .ToListAsync();

        if (waiting.Count == 0)
            return;

        var now = clock.UtcNow;
        var changed = false;

        foreach (var collection in waiting)
        {
            var outcome = await links.GetOutcomeAsync(collection.PaymentLinkId!, default);

            if (outcome.Paid)
            {
                // Para durumunu YALNIZ burası yazar — cihazdan gelen hiçbir alan
                // bu geçişi tetikleyemez.
                collection.Status = FieldCollectionStatus.Succeeded;
                collection.PaymentId = outcome.PaymentPublicId;
                collection.UpdatedAt = now;
                changed = true;
            }
            else if (outcome.Expired)
            {
                collection.Status = FieldCollectionStatus.Failed;
                collection.UpdatedAt = now;
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync();
    }
}
