using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Poyra.SharedKernel.Security;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class CredentialProtectorTests
{
    private static AesGcmCredentialProtector Create(string? keyBase64 = null)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Poyra:CredentialKey"] = keyBase64 ?? Convert.ToBase64String(new byte[32]),
            })
            .Build());

    [Fact]
    public void Sifrele_coz_gidis_donusu_ayni_degerleri_vermeli()
    {
        var protector = Create();
        var credentials = new Dictionary<string, string>
        {
            ["client_id"] = "400000200",
            ["store_key"] = "çok-gizli|karakter\\li",
        };

        var blob = protector.Protect(credentials);
        var restored = protector.Unprotect(blob);

        restored.ShouldBe(credentials);
    }

    [Fact]
    public void Ayni_veri_iki_sifrelemede_farkli_blob_uretmeli()
    {
        var protector = Create();
        var credentials = new Dictionary<string, string> { ["secret"] = "abc" };

        // Rastgele nonce → deterministik olmamalı
        protector.Protect(credentials).ShouldNotBe(protector.Protect(credentials));
    }

    [Fact]
    public void Yanlis_anahtar_cozememeli()
    {
        var blob = Create().Protect(new Dictionary<string, string> { ["secret"] = "abc" });

        var wrongKey = Create(Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()));
        Should.Throw<AuthenticationTagMismatchException>(() => wrongKey.Unprotect(blob));
    }

    [Fact]
    public void Eksik_anahtar_acik_hata_vermeli()
    {
        var protector = new AesGcmCredentialProtector(new ConfigurationBuilder().Build());
        var ex = Should.Throw<InvalidOperationException>(
            () => protector.Protect(new Dictionary<string, string>()));
        ex.Message.ShouldContain("Poyra:CredentialKey");
    }
}
