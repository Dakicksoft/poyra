using Poyra.Connectors.NestPay;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class NestPayHashTests
{
    private const string StoreKey = "TEST_STORE_KEY_123";

    private static Dictionary<string, string> SampleFields() => new()
    {
        ["clientid"] = "400000200",
        ["oid"] = "att_abc123",
        ["amount"] = "1499.00",
        ["currency"] = "949",
        ["okUrl"] = "http://localhost/cb",
        ["failUrl"] = "http://localhost/cb",
        ["rnd"] = "sabit-rnd",
        ["storetype"] = "3d_pay_hosting",
        ["trantype"] = "Auth",
        ["hashAlgorithm"] = "ver3",
        ["lang"] = "tr",
    };

    [Fact]
    public void Hash_deterministik_olmali_ve_alan_sirasi_onemsiz_olmali()
    {
        var fields = SampleFields();
        var shuffled = fields.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value);

        NestPayHash.ComputeVer3(fields, StoreKey)
            .ShouldBe(NestPayHash.ComputeVer3(shuffled, StoreKey));
    }

    [Fact]
    public void Callback_dogru_hashle_gecmeli_kurcalanmis_alanla_dusmeli()
    {
        // Banka dönüşünü taklit et: aynı ver3 şemasıyla imzalanmış form
        var callback = new Dictionary<string, string>(SampleFields())
        {
            ["mdStatus"] = "1",
            ["ProcReturnCode"] = "00",
            ["AuthCode"] = "123456",
        };
        callback["HASH"] = NestPayHash.ComputeVer3(callback, StoreKey);

        NestPayHash.ValidateCallback(callback, StoreKey).ShouldBeTrue();

        // Tutar kurcalanırsa imza düşmeli
        var tampered = new Dictionary<string, string>(callback) { ["amount"] = "1.00" };
        NestPayHash.ValidateCallback(tampered, StoreKey).ShouldBeFalse();

        // Yanlış store key ile düşmeli
        NestPayHash.ValidateCallback(callback, "YANLIS_KEY").ShouldBeFalse();
    }

    [Fact]
    public void Pipe_ve_ters_bolu_kacislanmali()
    {
        var withPipe = new Dictionary<string, string> { ["a"] = "x|y", ["b"] = @"c:\path" };
        var literal = new Dictionary<string, string> { ["a"] = "x", ["b"] = @"y|c:\path" };

        // Kaçışlama olmasaydı iki sözlük aynı düz metne düşerdi
        NestPayHash.ComputeVer3(withPipe, StoreKey)
            .ShouldNotBe(NestPayHash.ComputeVer3(literal, StoreKey));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("4", true)]
    [InlineData("0", false)]
    [InlineData("5", false)]
    [InlineData(null, false)]
    public void MdStatus_haritasi_dogru_olmali(string? mdStatus, bool expected)
        => NestPayErrorMap.IsThreeDsSuccessful(mdStatus).ShouldBe(expected);
}
