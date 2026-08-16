using Microsoft.Extensions.Configuration;
using Poyra.Modules.Tenancy.Infrastructure;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class JwtTokensTests
{
    private static JwtTokens Create(byte[]? key = null)
        => new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Poyra:JwtKey"] = Convert.ToBase64String(key ?? new byte[32]),
                })
                .Build(),
            new SystemClock());

    [Fact]
    public async Task Uret_dogrula_gidis_donusu_claimleri_korumali()
    {
        var jwt = Create();
        var principal = new JwtPrincipal(Guid.CreateVersion7(), "pos@ornek.com",
            Guid.CreateVersion7(), TenantRole.Operations);

        var token = jwt.Issue(principal);
        var validated = await jwt.ValidateAsync(token);

        validated.ShouldNotBeNull();
        validated.UserId.ShouldBe(principal.UserId);
        validated.Email.ShouldBe(principal.Email);
        validated.TenantId.ShouldBe(principal.TenantId);
        validated.Role.ShouldBe(TenantRole.Operations);
    }

    [Fact]
    public async Task Kurcalanmis_belirtec_reddedilmeli()
    {
        var jwt = Create();
        var token = jwt.Issue(new JwtPrincipal(Guid.NewGuid(), "a@b.c", Guid.NewGuid(), TenantRole.Owner));

        // imza kısmını boz
        var tampered = token[..^4] + "AAAA";
        (await jwt.ValidateAsync(tampered)).ShouldBeNull();
        (await jwt.ValidateAsync("tamamen-sacma")).ShouldBeNull();
    }

    [Fact]
    public async Task Farkli_anahtarla_imzalanan_belirtec_reddedilmeli()
    {
        var issuer = Create(Enumerable.Repeat((byte)1, 32).ToArray());
        var validator = Create(Enumerable.Repeat((byte)2, 32).ToArray());

        var token = issuer.Issue(new JwtPrincipal(Guid.NewGuid(), "a@b.c", Guid.NewGuid(), TenantRole.Admin));
        (await validator.ValidateAsync(token)).ShouldBeNull();
    }
}
