using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Recon.Infrastructure;

using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Recon;

public sealed class ReconModule
{
    public static readonly Assembly Assembly = typeof(ReconModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services
            .AddScoped<Poyra.Modules.Ledger.Contracts.ICommissionTerms, CommissionTerms>()
            .AddScoped<Poyra.Modules.Routing.Contracts.ICommissionRateSource, CommissionRateSource>()
            .AddScoped<Poyra.SharedKernel.Time.IBankHolidayCalendar, BankHolidayCalendar>()
            .AddScoped<StatementMatcher>()
            .AddScoped<StatementMatchJob>()
            .AddScoped<ReconStuckSweepJob>()
            .AddSingleton<IStatementParser, PoyraCsvStatementParser>()
            .AddSingleton<IStatementParser, NestPayCsvStatementParser>()
            .AddSingleton<IStatementParser, GvpCsvStatementParser>();
}
