using Hangfire;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Ledger.Contracts;
using Poyra.Modules.Ledger.Domain;
using Poyra.Modules.Tenancy.Contracts;
using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Ledger.Infrastructure;

/// <summary>
/// Tahsilatları <b>alacağa</b> dönüştürür: her çekim için bankanın işyerine ne borçlandığını,
/// ne kadar komisyon kesmesi gerektiğini ve parayı hangi gün ödemesi gerektiğini yazar.
///
/// <b>Neden arka planda:</b> alacak kaydı ödeme akışının içinde üretilseydi, defterdeki bir
/// hata müşteri kartın başındayken tahsilatı düşürürdü. Muhasebe kaydı, paranın tahsil
/// edilmesinden sonra gelir — hiçbir zaman önüne geçmez.
///
/// <b>Yeniden koşması güvenlidir:</b> alacak <c>(işyeri, att_, tür)</c> ile tekildir.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class ReceivableProjectionJob(
    LedgerDbContext db,
    TenantContext tenant,
    ITenantDirectory tenants,
    ICaptureFeed captures,
    ICommissionTerms terms,
    IBankHolidayCalendar holidays,
    IClock clock)
{
    public const int BatchSize = 500;

    /// <summary>
    /// İlk koşuda ne kadar geriye bakılacağı. Defter boşken tüm geçmişi taramak
    /// yerine son 30 gün alınır; daha eskisi için elle yeniden üretim yapılır.
    /// </summary>
    public static readonly TimeSpan ColdStartWindow = TimeSpan.FromDays(30);

    public async Task ProjectAsync()
    {
        var bankHolidays = await holidays.GetHolidaysAsync(default);

        foreach (var tenantId in await tenants.GetActiveTenantIdsAsync(default))
        {
            tenant.Set(tenantId);
            await ProjectForTenantAsync(bankHolidays);
        }
    }

    private async Task ProjectForTenantAsync(IReadOnlySet<DateOnly> bankHolidays)
    {
        // Su işareti defterin KENDİSİNDEN okunur — ayrı bir durum tablosu tutmak,
        // senkronu bozulabilecek ikinci bir gerçek kaynağı demektir.
        var watermark = await db.BankReceivables
            .OrderByDescending(r => r.CapturedAtServer)
            .Select(r => (DateTimeOffset?)r.CapturedAtServer)
            .FirstOrDefaultAsync()
            ?? clock.UtcNow - ColdStartWindow;

        await ProjectKindAsync(ReceivableKind.Sale,
            await captures.GetCapturedSinceAsync(watermark, BatchSize, default), bankHolidays);

        await ProjectKindAsync(ReceivableKind.Refund,
            await captures.GetRefundedSinceAsync(watermark, BatchSize, default), bankHolidays);
    }

    private async Task ProjectKindAsync(
        ReceivableKind kind, IReadOnlyList<CapturedCharge> charges, IReadOnlySet<DateOnly> bankHolidays)
    {
        if (charges.Count == 0)
            return;

        var incoming = charges.Select(c => c.AttemptPublicId).ToList();
        var already = await db.BankReceivables
            .Where(r => r.Kind == kind && incoming.Contains(r.AttemptPublicId))
            .Select(r => r.AttemptPublicId)
            .ToListAsync();

        var known = already.ToHashSet(StringComparer.Ordinal);
        var added = false;

        foreach (var charge in charges)
        {
            if (!known.Add(charge.AttemptPublicId))
                continue;

            var term = await terms.FindAsync(
                charge.ConnectorAccountId, charge.Installments, charge.CardBank, default);

            // İade alacağı AZALTIR: işaretler ters çevrilir ki toplam doğru çıksın
            var sign = kind == ReceivableKind.Refund ? -1 : 1;
            var gross = sign * Math.Abs(charge.GrossMinor);

            long? expectedCommission = term is null
                ? null
                : sign * BankReceivable.CommissionOf(Math.Abs(charge.GrossMinor), term.RateBps);

            db.BankReceivables.Add(new BankReceivable
            {
                TenantId = tenant.TenantId,
                ConnectorAccountId = charge.ConnectorAccountId,
                Kind = kind,
                AttemptPublicId = charge.AttemptPublicId,
                PaymentPublicId = charge.PaymentPublicId,
                GrossMinor = gross,
                Currency = charge.Currency,
                Installments = charge.Installments,
                CapturedAtServer = charge.CapturedAtServer,
                // Anlaşma yoksa BOŞ bırakılır — uydurulmuş bir oran, olmayan bir farkı
                // varmış gibi gösterir ve bankaya haksız itiraz yazdırır
                ExpectedRateBps = term?.RateBps,
                ExpectedCommissionMinor = expectedCommission,
                ExpectedNetMinor = expectedCommission is { } commission ? gross - commission : null,
                ExpectedValueDate = term is null
                    ? null
                    : BusinessCalendar.AddBusinessDays(
                        TurkeyTime.DayOf(charge.CapturedAtServer), term.ValorDays, bankHolidays),

                Status = ReceivableStatus.Expected,
            });

            added = true;
        }

        if (added)
        {
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Eşzamanlı bir tur aynı çekimi yazmış olabilir; tekillik kısıtı
                // beklenen korumadır, hata değil. Sonraki tur kaldığı yerden devam eder.
                db.ChangeTracker.Clear();
            }
        }
    }
}

/// <summary>
/// Beklenen valörü geçmiş ama banka hâlâ teyit etmemiş alacakları <c>overdue</c> yapar.
///
/// Ayrı bir iş, çünkü gecikme <b>zamanın geçmesiyle</b> oluşur — yeni bir tahsilat
/// gelmese de durum değişmelidir.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class OverdueReceivableJob(
    LedgerDbContext db, TenantContext tenant, ITenantDirectory tenants, IClock clock)
{
    public async Task FlagAsync()
    {
        var today = TurkeyTime.DayOf(clock.UtcNow);

        foreach (var tenantId in await tenants.GetActiveTenantIdsAsync(default))
        {
            tenant.Set(tenantId);

            var late = await db.BankReceivables
                .Where(r => r.Status == ReceivableStatus.Expected
                            && r.ExpectedValueDate != null && r.ExpectedValueDate < today)
                .ToListAsync();

            if (late.Count == 0)
                continue;

            foreach (var receivable in late)
            {
                receivable.Status = ReceivableStatus.Overdue;
                receivable.UpdatedAt = clock.UtcNow;
            }

            await db.SaveChangesAsync();
        }
    }
}
