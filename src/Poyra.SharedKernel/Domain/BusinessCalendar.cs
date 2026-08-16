namespace Poyra.SharedKernel.Domain;


public static class BusinessCalendar
{
    /// <summary>
    /// startExclusive'den itibaren n İŞ GÜNÜ ilerler (hafta sonu + tatiller atlanır).
    /// n=0 → başlangıç günü aynen döner (valör 0 = aynı gün ).
    /// </summary>
    public static DateOnly AddBusinessDays(DateOnly startExclusive, int businessDays, IReadOnlySet<DateOnly> holidays)
    {
        var day = startExclusive;
        var remaining = businessDays;
        while (remaining > 0)
        {
            day = day.AddDays(1);
            if (IsBusinessDay(day, holidays))
                remaining--;
        }

        return day;
    }

    public static bool IsBusinessDay(DateOnly day, IReadOnlySet<DateOnly> holidays)
        => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(day);
}
