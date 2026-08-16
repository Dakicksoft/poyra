using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Disputes.Domain;

public static class DisputeDeadline
{
    public static readonly IReadOnlyDictionary<DisputeStage, int> BusinessDaysByStage =
        new Dictionary<DisputeStage, int>
        {
            [DisputeStage.Retrieval] = 7,
            [DisputeStage.Chargeback] = 9,
            [DisputeStage.PreArbitration] = 7,
            [DisputeStage.Arbitration] = 5,
        };

    public static readonly TimeSpan WarningWindow = TimeSpan.FromDays(3);

    public static DateTimeOffset Compute(
        DateTimeOffset openedAt, DisputeStage stage, IReadOnlySet<DateOnly> holidays)
    {
        var openedTr = DateOnly.FromDateTime(openedAt.ToOffset(TimeSpan.FromHours(3)).DateTime);
        var due = BusinessCalendar.AddBusinessDays(openedTr, BusinessDaysByStage[stage], holidays);

        // timestamptz'a yazılacak: Npgsql yalnız UTC ofseti kabul eder
        return new DateTimeOffset(due.ToDateTime(TimeOnly.MaxValue), TimeSpan.FromHours(3))
            .ToUniversalTime();
    }
}
