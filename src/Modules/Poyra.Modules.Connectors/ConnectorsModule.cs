using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Gvp;
using Poyra.Connectors.NestPay;
using Poyra.Connectors.PayFlex;
using Poyra.Connectors.Posnet;
using Poyra.Connectors.Adyen;
using Poyra.Connectors.Stripe;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Connectors.Infrastructure;

namespace Poyra.Modules.Connectors;

public sealed class ConnectorsModule
{
    public static readonly Assembly Assembly = typeof(ConnectorsModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddHttpClient(NestPayConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(GvpConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(PayFlexConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(PosnetConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(StripeConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(AdyenConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.Boa.BoaConnectorBase.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.CCPayment.CCPaymentConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.Iyzico.IyzicoConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.Moka.MokaConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.Payten.PaytenConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.PayNKolay.PayNKolayConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.Tami.TamiConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.AhlPay.AhlPayConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.ParamPos.ParamPosConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.PayTR.PayTRConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.Craftgate.CraftgateConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.PayFor.PayForConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Poyra.Connectors.InterVpos.InterVposConnector.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddKeyedSingleton<IPaymentConnector, MockBankConnector>(MockBankConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, NestPayConnector>(NestPayConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, GvpConnector>(GvpConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, PayFlexConnector>(PayFlexConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, PosnetConnector>(PosnetConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, StripeConnector>(StripeConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, AdyenConnector>(AdyenConnector.ConnectorKey);

        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.InterVpos.InterVposConnector>(
            Poyra.Connectors.InterVpos.InterVposConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.Boa.KuveytTurkConnector>(
            Poyra.Connectors.Boa.KuveytTurkConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.Boa.VakifKatilimConnector>(
            Poyra.Connectors.Boa.VakifKatilimConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.CCPayment.CCPaymentConnector>(
            Poyra.Connectors.CCPayment.CCPaymentConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.Iyzico.IyzicoConnector>(
            Poyra.Connectors.Iyzico.IyzicoConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.Moka.MokaConnector>(
            Poyra.Connectors.Moka.MokaConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.Payten.PaytenConnector>(
            Poyra.Connectors.Payten.PaytenConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.PayFor.PayForConnector>(
            Poyra.Connectors.PayFor.PayForConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.PayNKolay.PayNKolayConnector>(
            Poyra.Connectors.PayNKolay.PayNKolayConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.Tami.TamiConnector>(
            Poyra.Connectors.Tami.TamiConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.AhlPay.AhlPayConnector>(
            Poyra.Connectors.AhlPay.AhlPayConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.ParamPos.ParamPosConnector>(
            Poyra.Connectors.ParamPos.ParamPosConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.PayTR.PayTRConnector>(
            Poyra.Connectors.PayTR.PayTRConnector.ConnectorKey);
        services.AddKeyedSingleton<IPaymentConnector, Poyra.Connectors.Craftgate.CraftgateConnector>(
            Poyra.Connectors.Craftgate.CraftgateConnector.ConnectorKey);
        services.AddSingleton(sp => new ConnectorRegistry(sp,
            [MockBankConnector.ConnectorKey, NestPayConnector.ConnectorKey,
             GvpConnector.ConnectorKey, PayFlexConnector.ConnectorKey,
             PosnetConnector.ConnectorKey, StripeConnector.ConnectorKey,
             AdyenConnector.ConnectorKey,
             Poyra.Connectors.InterVpos.InterVposConnector.ConnectorKey,
             Poyra.Connectors.Boa.KuveytTurkConnector.ConnectorKey,
             Poyra.Connectors.Boa.VakifKatilimConnector.ConnectorKey,
             Poyra.Connectors.CCPayment.CCPaymentConnector.ConnectorKey,
             Poyra.Connectors.Iyzico.IyzicoConnector.ConnectorKey,
             Poyra.Connectors.Moka.MokaConnector.ConnectorKey,
             Poyra.Connectors.Payten.PaytenConnector.ConnectorKey,
             Poyra.Connectors.PayFor.PayForConnector.ConnectorKey,
             Poyra.Connectors.PayNKolay.PayNKolayConnector.ConnectorKey,
             Poyra.Connectors.Tami.TamiConnector.ConnectorKey,
             Poyra.Connectors.AhlPay.AhlPayConnector.ConnectorKey,
             Poyra.Connectors.ParamPos.ParamPosConnector.ConnectorKey,
             Poyra.Connectors.PayTR.PayTRConnector.ConnectorKey,
             Poyra.Connectors.Craftgate.CraftgateConnector.ConnectorKey]));
        services.AddScoped<ConnectorCanaryJob>();

        services.AddScoped<IConnectorGateway, ConnectorGateway>();
        services.AddScoped<IConnectorAccountsDirectory, ConnectorAccountsDirectory>();
        return services;
    }
}
