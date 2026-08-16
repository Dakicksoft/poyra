using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Field.Contracts;
using Poyra.Modules.PaymentLinks.Contracts;
using Poyra.Modules.PaymentLinks.Infrastructure;

namespace Poyra.Modules.PaymentLinks;

public sealed class PaymentLinksModule
{
    public static readonly Assembly Assembly = typeof(PaymentLinksModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services
            .AddScoped<ICheckoutLinkResolver, CheckoutLinkResolver>()
            .AddScoped<IFieldCheckoutLinks, FieldCheckoutLinks>()
            .AddScoped<PaymentLinkOutcomeJob>();
}
