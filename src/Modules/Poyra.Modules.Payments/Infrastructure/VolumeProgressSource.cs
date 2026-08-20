using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Routing.Contracts;

namespace Poyra.Modules.Payments.Infrastructure;

/// <summary>
/// Hacim taahhüdünün ilerlemesi: dönem içinde her hesaba gerçekte ne kadar iş gitti.
///
/// Sayım TAHSİL EDİLMİŞ denemeler üzerinden yapılır — banka taahhüdü başarısız
/// denemelerle değil, geçen ciroyla ölçer. Tutar da <b>çekilen</b> tutardır (vade farkı
/// dahil): ekstrede o rakam görünür, taahhüt de onun üzerinden sayılır.
///
/// İadeler düşülmez: banka taahhüt sayımında iadeyi genelde ayrı işler ve düşüldüğünü
/// varsaymak taahhüdü OLDUĞUNDAN AZ gösterir — rota gereğinden fazla hacmi o POS'a
/// yığar. Eksik saymak yerine ham ciro sayılır; sapma varsa işyeri hedefi ayarlar.
/// </summary>
public sealed class VolumeProgressSource(PaymentsDbContext db) : IVolumeProgressSource
{
    public async Task<IReadOnlyList<ConnectorVolume>> GetAsync(
        DateTimeOffset periodStart, CancellationToken ct)
        => await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.Status == AttemptStatus.Captured
                        && a.CapturedAt != null && a.CapturedAt >= periodStart)
            .GroupBy(a => a.ConnectorAccountId)
            .Select(g => new ConnectorVolume(g.Key, g.Sum(a => a.AmountMinor)))
            .ToListAsync(ct);
}
