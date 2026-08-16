using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Gvp;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class GvpHashTests
{
    private const string StoreKey = "12345678";

    [Fact]
    public void Terminal_id_9_haneye_sifir_dolgulanmali()
    {
        GvpHash.PadTerminalId("30691297").ShouldBe("030691297");
        GvpHash.PadTerminalId("123456789").ShouldBe("123456789");
    }

    [Fact]
    public void HashedPassword_buyuk_hex_sha1_olmali()
    {
        var hash = GvpHash.HashedPassword("123qweASD/", "30691297");

        hash.Length.ShouldBe(40); // SHA-1 → 20 bayt → 40 hex
        hash.ShouldBe(hash.ToUpperInvariant());
        GvpHash.HashedPassword("123qweASD/", "30691297").ShouldBe(hash); // deterministik
        GvpHash.HashedPassword("baska", "30691297").ShouldNotBe(hash);
    }

    [Fact]
    public void Uc_d_istek_hashi_deterministik_ve_alan_duyarli_olmali()
    {
        var hashedPassword = GvpHash.HashedPassword("sifre", "30691297");
        string Compute(string amount) => GvpHash.ThreeDsRequestHash(
            "30691297", "att_abc", amount, "949",
            "http://localhost/cb", "http://localhost/cb", "sales", "3", StoreKey, hashedPassword);

        Compute("149900").ShouldBe(Compute("149900"));
        Compute("149900").ShouldNotBe(Compute("1")); // tutar hash'e dahil
        Compute("149900").Length.ShouldBe(128); // SHA-512 hex
    }

    [Fact]
    public void Callback_dogrulama_sha512_yolunda_gecmeli_kurcalamada_dusmeli()
    {
        var form = new Dictionary<string, string>
        {
            ["orderid"] = "att_abc",
            ["mdstatus"] = "1",
            ["procreturncode"] = "00",
            ["authcode"] = "123456",
            ["hashparams"] = "orderid:mdstatus:procreturncode",
        };
        var plain = form["orderid"] + form["mdstatus"] + form["procreturncode"] + StoreKey;
        form["hash"] = Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(plain)));

        GvpHash.ValidateCallback(form, StoreKey).ShouldBeTrue();

        var tampered = new Dictionary<string, string>(form) { ["procreturncode"] = "05" };
        GvpHash.ValidateCallback(tampered, StoreKey).ShouldBeFalse();
        GvpHash.ValidateCallback(form, "YANLIS").ShouldBeFalse();
    }

    [Fact]
    public void Callback_dogrulama_eski_sha1_yolunu_da_desteklemeli()
    {
        var form = new Dictionary<string, string>
        {
            ["orderid"] = "att_abc",
            ["mdstatus"] = "1",
            ["hashparams"] = "orderid:mdstatus",
        };
        var plain = form["orderid"] + form["mdstatus"] + StoreKey;
        form["hash"] = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(plain)));

        GvpHash.ValidateCallback(form, StoreKey).ShouldBeTrue();
    }

    [Fact]
    public void Hash_veya_hashparams_yoksa_reddedilmeli()
    {
        GvpHash.ValidateCallback(new Dictionary<string, string> { ["hash"] = "AA" }, StoreKey).ShouldBeFalse();
        GvpHash.ValidateCallback(new Dictionary<string, string> { ["hashparams"] = "a" }, StoreKey).ShouldBeFalse();
    }
}
