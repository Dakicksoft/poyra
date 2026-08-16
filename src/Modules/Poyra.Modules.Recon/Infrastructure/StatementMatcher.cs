using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Recon.Domain;
using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Recon.Infrastructure;

/// <summary>
/// Eşleştirme + komisyon denetimi + valör hesabının tek çekirdeği — küçük ekstrede
/// senkron (handler), büyükte Hangfire (StatementMatchJob) aynı kodu çağırır.
/// Satırlar Pending durumuyla yazılmış olmalıdır; bu sınıf durumlarını kesinleştirir.
/// </summary>
public sealed class StatementMatcher(
    ReconDbContext db,
    IPaymentLedger ledger,
    Poyra.Modules.Ledger.Contracts.IReceivableConfirmation receivables)
{
    public async Task MatchAsync(Guid statementId, CancellationToken ct)
    {
        var statement = await db.ReconStatements.SingleAsync(s => s.Id == statementId, ct);
        var lines = await db.ReconStatementLines
            .Where(l => l.StatementId == statementId)
            .OrderBy(l => l.LineNo)
            .ToListAsync(ct);

        var agreements = await db.CommissionAgreements.AsNoTracking()
            .Where(a => a.ConnectorAccountId == statement.ConnectorAccountId)
            .ToDictionaryAsync(a => a.InstallmentCount, ct);

        // İş günü valör takvimi: hafta sonu + banka tatilleri (platform tablosu)
        var holidays = (await db.BankHolidays.AsNoTracking().Select(h => h.Day).ToListAsync(ct))
            .ToHashSet();

        // İdempotentlik: bulgular silinemez (İlke 3) → yeniden koşuda var olan satır atlanır
        var auditedLineIds = (await db.CommissionAuditFindings.AsNoTracking()
                .Where(f => f.StatementId == statementId)
                .Select(f => f.StatementLineId)
                .ToListAsync(ct))
            .ToHashSet();

        // Sayaçlar sıfırdan kurulur — yeniden eşleştirme (replay/süpürme) güvenli olsun
        statement.RefundLineCount = 0;
        statement.MatchedCount = 0;
        statement.MissingInPoyraCount = 0;
        statement.AmountMismatchCount = 0;
        statement.RefundCommissionSum = 0;

        var matchedSaleOrderIds = new HashSet<string>(StringComparer.Ordinal);
        var consumedRefundIds = new HashSet<Guid>(); // aynı iade iki satıra eşlenmesin

        foreach (var line in lines)
        {
            if (line.LineType == StatementLineType.Refund)
            {
                statement.RefundLineCount++;
                await MatchRefundLineAsync(line, statement, consumedRefundIds, ct);
                continue;
            }

            await MatchSaleLineAsync(line, statement, agreements, holidays, auditedLineIds, matchedSaleOrderIds, ct);
        }

        // Defterde olup ekstrede olmayanlar (o TR gününün tahsilatları)
        var dayCaptures = await ledger.GetCapturedForDayAsync(
            statement.ConnectorAccountId, statement.StatementDate, ct);
        statement.MissingInStatementCount = dayCaptures.Count(c => !matchedSaleOrderIds.Contains(c.PublicId));

        statement.Status = StatementStatus.Matched;
        await db.SaveChangesAsync(ct);
    }

    private async Task MatchSaleLineAsync(
        ReconStatementLine line,
        ReconStatement statement,
        IReadOnlyDictionary<int, CommissionAgreement> agreements,
        IReadOnlySet<DateOnly> holidays,
        IReadOnlySet<Guid> auditedLineIds,
        HashSet<string> matchedSaleOrderIds,
        CancellationToken ct)
    {
        var attempt = await ledger.FindCapturedByOrderIdAsync(line.OrderId, ct);
        if (attempt is null || attempt.ConnectorAccountId != statement.ConnectorAccountId)
        {
            line.MatchStatus = LineMatchStatus.MissingInPoyra;
            statement.MissingInPoyraCount++;
            return;
        }

        line.MatchedAttemptId = attempt.AttemptId;
        matchedSaleOrderIds.Add(attempt.PublicId);

        if (attempt.ChargedAmountMinor != line.GrossMinor)
        {
            line.MatchStatus = LineMatchStatus.AmountMismatch;
            statement.AmountMismatchCount++;
            return; // tutar tutmayan satırda komisyon/valör denetimi anlamsız
        }

        line.MatchStatus = LineMatchStatus.Matched;
        statement.MatchedCount++;

        var agreement = agreements.GetValueOrDefault(attempt.Installments);

        // Valör: beklenen hesaba geçiş = ekstre günü + anlaşma valörü kadar İŞ GÜNÜ
        // (hafta sonu + banka tatilleri atlanır)
        if (agreement is not null)
        {
            line.ExpectedValueDate = BusinessCalendar.AddBusinessDays(
                statement.StatementDate, agreement.ValorDays, holidays);
            if (line.ValueDate is { } valueDate)
                line.ValorDeltaDays = valueDate.DayNumber - line.ExpectedValueDate.Value.DayNumber;
        }

        // DEFTERE HABER VER: banka bu alacağı kabul etti ve GERÇEK komisyonunu söyledi.
        // Bulgu yazımından ÖNCE ve ondan bağımsız yapılır — ekstre yeniden işlendiğinde
        // bulgu tekrarlanmaz ama teyidin yenilenmesi zararsızdır (aynı değerler yazılır).
        //
        // Yön önemli: mutabakat deftere HABER VERİR, defter mutabakata sormaz. Böylece
        // defter hiçbir modüle bağlı kalmaz.
        await receivables.ConfirmAsync(
            attempt.PublicId, line.CommissionMinor, line.ValueDate, ct);

        if (auditedLineIds.Contains(line.Id))
            return; // yeniden koşu: bulgu zaten yazılmış (silinemez) — çift kayıt açma

        // Komisyon denetimi: anlaşma oranından beklenen ↔ bankanın kestiği
        long? expected = agreement is null
            ? null
            : ReconMath.ExpectedCommission(line.GrossMinor, agreement.RateBps);

        db.CommissionAuditFindings.Add(new CommissionAuditFinding
        {
            TenantId = statement.TenantId,
            StatementId = statement.Id,
            StatementLineId = line.Id,
            AttemptId = attempt.AttemptId,
            Installments = attempt.Installments,
            GrossMinor = line.GrossMinor,
            ActualCommissionMinor = line.CommissionMinor,
            ExpectedCommissionMinor = expected,
            AgreedRateBps = agreement?.RateBps,
            DeltaMinor = expected is { } exp ? line.CommissionMinor - exp : null,
        });
    }

    private async Task MatchRefundLineAsync(
        ReconStatementLine line,
        ReconStatement statement,
        HashSet<Guid> consumedRefundIds,
        CancellationToken ct)
    {
        // İade satırı orijinal satışın sipariş numarasını taşır; tutar üzerinden iadeyle eşlenir
        var refunds = await ledger.GetSucceededRefundsByAttemptOrderIdAsync(line.OrderId, ct);
        var match = refunds.FirstOrDefault(r =>
            r.AmountMinor == line.GrossMinor && !consumedRefundIds.Contains(r.RefundId));

        if (match is null)
        {
            line.MatchStatus = LineMatchStatus.MissingInPoyra;
            statement.MissingInPoyraCount++;
            return;
        }

        consumedRefundIds.Add(match.RefundId);
        line.MatchedRefundId = match.RefundId;
        line.MatchStatus = LineMatchStatus.Matched;
        statement.MatchedCount++;

        // İade komisyonu: çoğu anlaşmada 0 beklenir — kesilen toplanır, raporda görünür
        // (banka politikasına bağlı beklenen-iade-komisyonu denetimi F3)
        statement.RefundCommissionSum += line.CommissionMinor;
    }
}
