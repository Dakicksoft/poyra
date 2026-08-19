using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Installments.Contracts;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Routing.Contracts;

namespace Poyra.Modules.Payments.Infrastructure;

public sealed class HistoricPaymentSource(PaymentsDbContext db, IBinLookup bins) : IHistoricPaymentSource
{
    public async Task<IReadOnlyList<HistoricPayment>> GetAsync(
        DateTimeOffset since, int limit, CancellationToken ct)
    {
        var rows = await (
                from intent in db.PaymentIntents.AsNoTracking()
                join attempt in db.PaymentAttempts.AsNoTracking()
                    on intent.Id equals attempt.PaymentIntentId
                where intent.CreatedAt >= since && attempt.AttemptNo == 1
                orderby intent.CreatedAt descending
                select new
                {
                    intent.Id,
                    intent.PublicId,
                    intent.AmountMinor,
                    intent.Currency,
                    intent.Installments,
                    intent.CreatedAt,
                    attempt.ConnectorAccountId,
                    attempt.MaskedPan,
                    AttemptedAt = attempt.CreatedAt,
                })
            .Take(Math.Clamp(limit, 1, 5_000))
            .ToListAsync(ct);

        var result = new List<HistoricPayment>(rows.Count);
        var binCache = new Dictionary<string, CardFacts?>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            CardFacts? card = null;
            if (ExtractBin(row.MaskedPan) is { } bin)
            {
                if (!binCache.TryGetValue(bin, out card))
                {
                    var info = await bins.FindAsync(bin, ct);
                    card = info is null
                        ? new CardFacts(bin, null, null, null, null, false)
                        : new CardFacts(info.Bin, info.BankCode, info.Program, info.Brand,
                            info.CardType, info.IsCommercial);
                    binCache[bin] = card;
                }
            }

            // Saat sinyali karar anına en yakın damgadan: rota kararı confirm isteğinde,
            // ilk deneme açılmadan hemen önce verilir — intent'in oluşturulma saati değil.
            result.Add(new HistoricPayment(
                row.PublicId, row.Id, row.AmountMinor, row.Currency, row.Installments,
                row.AttemptedAt.ToOffset(TimeSpan.FromHours(3)).Hour, // TR saati
                card, row.ConnectorAccountId, ActualCostMinor: null, row.CreatedAt));
        }

        return result;
    }

    private static string? ExtractBin(string? maskedPan)
    {
        if (maskedPan is null || maskedPan.Length < 6)
            return null;

        var prefix = maskedPan[..6];
        return prefix.All(char.IsAsciiDigit) ? prefix : null;
    }
}
