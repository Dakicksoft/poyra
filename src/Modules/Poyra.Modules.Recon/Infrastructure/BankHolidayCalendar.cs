using Microsoft.EntityFrameworkCore;

using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Recon.Infrastructure;

public sealed class BankHolidayCalendar(ReconDbContext db) : IBankHolidayCalendar
{
    public async Task<IReadOnlySet<DateOnly>> GetHolidaysAsync(CancellationToken ct)
        => (await db.BankHolidays.AsNoTracking().Select(h => h.Day).ToListAsync(ct)).ToHashSet();
}
