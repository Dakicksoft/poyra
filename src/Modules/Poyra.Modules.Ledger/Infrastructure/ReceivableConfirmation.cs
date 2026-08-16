using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Ledger.Contracts;
using Poyra.Modules.Ledger.Domain;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Ledger.Infrastructure;

/// <summary>
/// Mutabakat "banka bu alacağı kabul etti" dediğinde çalışır.
///
/// Buradan sonra alacağın iki sayısı olur: <b>bizim iddiamız</b> (anlaşmadan) ve
/// <b>bankanın söylediği</b> (ekstreden). İkisini birleştirip tek sayıya indirmek,
/// aradaki farkı — yani ürünün asıl değerini — yok etmek olurdu.
/// </summary>
public sealed class ReceivableConfirmation(LedgerDbContext db, IClock clock) : IReceivableConfirmation
{
    public async Task ConfirmAsync(
        string attemptPublicId, long confirmedCommissionMinor, DateOnly? confirmedValueDate,
        CancellationToken ct)
    {
        var receivable = await db.BankReceivables
            .SingleOrDefaultAsync(r => r.AttemptPublicId == attemptPublicId
                                       && r.Kind == ReceivableKind.Sale, ct);

        // Alacak henüz üretilmemiş olabilir (projeksiyon işi ekstreden sonra koştu).
        // Sessizce geç: bir sonraki turda alacak doğar, ekstre yeniden işlendiğinde teyit gelir.
        if (receivable is null)
            return;

        receivable.ConfirmedCommissionMinor = confirmedCommissionMinor;
        receivable.ConfirmedNetMinor = receivable.GrossMinor - confirmedCommissionMinor;
        receivable.ConfirmedValueDate = confirmedValueDate;
        receivable.BankConfirmedAt = clock.UtcNow;

        // Gecikmiş bir alacak teyit gelince gecikmiş olmaktan ÇIKAR: banka geç de olsa
        // borcu kabul etti. Paranın gerçekten geldiği ayrı bir sorudur (üç yönlü mutabakat).
        receivable.Status = ReceivableStatus.BankConfirmed;
        receivable.UpdatedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);
    }
}
