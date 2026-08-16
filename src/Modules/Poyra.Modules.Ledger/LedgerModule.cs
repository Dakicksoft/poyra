using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Ledger.Contracts;
using Poyra.Modules.Ledger.Infrastructure;

namespace Poyra.Modules.Ledger;

public sealed class LedgerModule
{
    public static readonly Assembly Assembly = typeof(LedgerModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services
            .AddScoped<IReceivableConfirmation, ReceivableConfirmation>()
            .AddScoped<SettlementMatcher>()
            .AddScoped<ISettlementParser, PoyraCsvSettlementParser>()
            .AddScoped<ISettlementParser, Mt940SettlementParser>()
            .AddScoped<ReceivableProjectionJob>()
            .AddScoped<OverdueReceivableJob>();
}
