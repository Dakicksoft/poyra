using Poyra.Modules.Tenancy.Security;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class ApiKeysTests
{
    [Fact]
    public void Generate_test_onekiyle_ve_yeterli_entropiyle_uretmeli()
    {
        var key = ApiKeys.Generate();

        key.ShouldStartWith(ApiKeys.TestPrefix);
        key.Length.ShouldBeGreaterThan(40); // 32 bayt base64url ≈ 43 karakter + önek
        ApiKeys.Generate().ShouldNotBe(key);
    }

    [Fact]
    public void Hash_deterministik_sha512_hex_uretmeli()
    {
        const string key = "sk_test_sabit-anahtar";

        var hash1 = ApiKeys.Hash(key);
        var hash2 = ApiKeys.Hash(key);

        hash1.ShouldBe(hash2);
        hash1.Length.ShouldBe(128); // SHA-512 → 64 bayt → 128 hex karakter
        ApiKeys.Hash("sk_test_baska").ShouldNotBe(hash1);
    }

    [Fact]
    public void PrefixHint_ilk_12_karakteri_vermeli()
    {
        var key = ApiKeys.Generate();

        ApiKeys.PrefixHint(key).ShouldBe(key[..12]);
    }
}
