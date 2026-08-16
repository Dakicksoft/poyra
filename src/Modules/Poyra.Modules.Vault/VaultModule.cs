using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Modules.Vault.Contracts;
using Poyra.Modules.Vault.Infrastructure;

namespace Poyra.Modules.Vault;

public sealed class VaultModule
{
    public static readonly Assembly Assembly = typeof(VaultModule).Assembly;

    public static IServiceCollection Add(IServiceCollection services)
        => services
            .AddScoped<Contracts.ICustomerCardSource, Infrastructure.CustomerCardSource>()
            .AddSingleton<VaultCrypto>()
            .AddScoped<ICardVault, CardVault>();
}
