using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Ledger.Domain;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Ledger.Infrastructure;

/// <summary>
/// <b>Üç yönlü mutabakatın çekirdeği:</b> "söz verilen para gerçekten geldi mi?"
///
/// <b>Neden satır-satır değil, gün-küme:</b> banka POS hasılatını tek tek değil
/// TOPLU yatırır — bir hesap hareketi yüzlerce tahsilatın netidir. Bu yüzden
/// karşılaştırma bir POS hesabının bir VALÖR GÜNÜ için yapılır:
///
/// <code>
///   alacaklardan beklenen net toplam   ↔   o gün hesaba yatan toplam
/// </code>
///
/// Eşitse alacaklar <c>settled</c> olur — zincirin son halkası kapanır. Eşit değilse
/// bulgu yazılır ve <b>bankadan sorulacak para</b> ortaya çıkar.
/// </summary>
public sealed class SettlementMatcher(LedgerDbContext db, IClock clock)
{
    public async Task MatchAsync(Guid statementId, CancellationToken ct = default)
    {
        var statement = await db.SettlementStatements.SingleAsync(s => s.Id == statementId, ct);

        var lines = await db.SettlementLines.AsNoTracking()
            .Where(l => l.StatementId == statementId)
            .ToListAsync(ct);

        if (lines.Count == 0)
            return;

        // Aynı ekstre yeniden yüklenirse bulgu ÇİFTLENMESİN. Bulgu silinemez (İlke 3),
        // o yüzden var olanı atlamak tek doğru yol.
        var alreadyFound = (await db.SettlementFindings.AsNoTracking()
                .Where(f => f.StatementId == statementId)
                .Select(f => f.ValueDate)
                .ToListAsync(ct))
            .ToHashSet();

        var now = clock.UtcNow;

        // Hesap hareketleri valör gününe göre toplanır: bir günde birden çok yatış
        // olabilir (banka parçalı gönderir) ve bunların toplamı o günün hasılatıdır.
        var actualByDay = lines
            .GroupBy(l => l.ValueDate)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.AmountMinor));

        // Beklenen taraf: o POS hesabının o valör gününe düşen TÜM alacakları.
        // İadeler negatif yazıldığı için toplam kendiliğinden netleşir — banka
        // hasılattan iadeyi mahsup ettiğinde tutar yine tutar.
        var days = actualByDay.Keys.ToList();
        var receivables = await db.BankReceivables
            .Where(r => r.ConnectorAccountId == statement.ConnectorAccountId
                        && r.ExpectedValueDate != null
                        && days.Contains(r.ExpectedValueDate.Value)
                        && r.Status != ReceivableStatus.Reversed)
            .ToListAsync(ct);

        var byDay = receivables
            .GroupBy(r => r.ExpectedValueDate!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (valueDate, actual) in actualByDay)
        {
            if (alreadyFound.Contains(valueDate))
                continue;

            var group = byDay.GetValueOrDefault(valueDate, []);

            // Teyit varsa bankanın söylediği net, yoksa anlaşmadan beklenen.
            // Brüte düşmek YANLIŞ olurdu: komisyon zaten kesilerek yatar.
            var expected = group.Sum(r => r.ConfirmedNetMinor ?? r.ExpectedNetMinor ?? 0);

            var outcome = SettlementFinding.Classify(expected, actual);

            db.SettlementFindings.Add(new SettlementFinding
            {
                TenantId = statement.TenantId,
                ConnectorAccountId = statement.ConnectorAccountId,
                StatementId = statementId,
                ValueDate = valueDate,
                ExpectedMinor = expected,
                ActualMinor = actual,
                DeltaMinor = actual - expected,
                ReceivableCount = group.Count,
                Outcome = outcome,
            });

            // Zincirin son halkası: para GERÇEKTEN geldiyse alacak kapanır.
            // Eksik yattıysa KAPATILMAZ — kapatmak, bankadan sorulacak parayı
            // "tahsil edildi" diye gömmek olurdu.
            if (outcome == SettlementOutcome.Settled)
            {
                foreach (var receivable in group)
                {
                    receivable.Status = ReceivableStatus.Settled;
                    receivable.UpdatedAt = now;
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
