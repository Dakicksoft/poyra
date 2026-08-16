using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Ledger.Contracts;
using Poyra.Modules.Payments.Features;
using Poyra.Modules.Payments.Infrastructure;

namespace Poyra.Modules.Payments;

public sealed class PaymentsModule
{
    public static readonly Assembly Assembly = typeof(PaymentsModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services
            .AddScoped<ICaptureFeed, CaptureFeed>()
            .AddSingleton<PaymentMapper>()
            .AddScoped<OutboxDispatcher>()
            .AddScoped<ThreeDsTimeoutJob>()
            .AddScoped<IOutboxNudger, HangfireOutboxNudger>()
            .AddScoped<Contracts.IPaymentLedger, PaymentLedger>()
            .AddScoped<Contracts.IPaymentLookup, PaymentLookup>()
            .AddScoped<Contracts.IPaymentVelocitySource, PaymentVelocitySource>()
            .AddScoped<Contracts.ICustomerPaymentSource, CustomerPaymentSource>()
            .AddScoped<Contracts.IRiskGate, PermissiveRiskGate>()
            .AddScoped<Contracts.IPaymentInitiator, PaymentInitiator>()
            .AddScoped<Routing.Contracts.IConnectorPerformanceSource, ConnectorPerformanceSource>()
            .AddScoped<Routing.Contracts.IHistoricPaymentSource, HistoricPaymentSource>();
}
