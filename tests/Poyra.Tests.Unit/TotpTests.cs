using Poyra.Modules.Tenancy.Security;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class TotpTests
{
    // RFC 6238 Ek B test vektörleri: ASCII "12345678901234567890" sırrı
    // Base32 karşılığı GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ; 8 haneli vektörün son 6 hanesi.
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59, "287082")]          // 1970-01-01T00:00:59 → 94287082
    [InlineData(1111111109, "081804")]  // 2005-03-18T01:58:29 → 07081804
    [InlineData(1234567890, "005924")]  // 2009-02-13T23:31:30 → 89005924
    [InlineData(2000000000, "279037")]  // 2033-05-18T03:33:20 → 69279037
    public void Verify_rfc6238_vektorlerini_dogrulamali(long unixSeconds, string code)
        => Totp.Verify(RfcSecret, code, DateTimeOffset.FromUnixTimeSeconds(unixSeconds))
            .ShouldBeTrue();

    [Fact]
    public void Verify_yanlis_kodu_reddetmeli()
        => Totp.Verify(RfcSecret, "000000", DateTimeOffset.FromUnixTimeSeconds(59))
            .ShouldBeFalse();

    [Fact]
    public void Verify_bir_onceki_adimin_kodunu_tolere_etmeli()
        // 59. saniyenin kodu, 61. saniyede (bir adım sonra) hâlâ geçmeli — saat kayması
        => Totp.Verify(RfcSecret, "287082", DateTimeOffset.FromUnixTimeSeconds(61))
            .ShouldBeTrue();

    [Fact]
    public void Verify_iki_adim_oncesini_reddetmeli()
        // 59. saniyenin kodu 59+90 saniyede (iki adım sonra) artık geçmemeli
        => Totp.Verify(RfcSecret, "287082", DateTimeOffset.FromUnixTimeSeconds(59 + 90))
            .ShouldBeFalse();

    [Fact]
    public void Verify_hane_sayisi_tutmayan_girdiyi_reddetmeli()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(59);
        Totp.Verify(RfcSecret, "28708", now).ShouldBeFalse();
        Totp.Verify(RfcSecret, "28708a", now).ShouldBeFalse();
        Totp.Verify(RfcSecret, "", now).ShouldBeFalse();
    }

    [Fact]
    public void GenerateSecret_base32_ve_yeterli_uzunlukta_olmali()
    {
        var secret = Totp.GenerateSecret();

        secret.Length.ShouldBe(32); // 20 bayt → 32 Base32 karakteri
        secret.ShouldAllBe(c => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(c));
        Totp.GenerateSecret().ShouldNotBe(secret);
    }

    [Fact]
    public void BuildOtpauthUri_hesap_ve_ihraccisini_tasimali()
    {
        var uri = Totp.BuildOtpauthUri("sahip@ornek.com", RfcSecret);

        uri.ShouldStartWith("otpauth://totp/Poyra:sahip%40ornek.com");
        uri.ShouldContain($"secret={RfcSecret}");
        uri.ShouldContain("issuer=Poyra");
        uri.ShouldContain("digits=6");
        uri.ShouldContain("period=30");
    }

    [Fact]
    public void Verify_elle_girilen_bosluklu_sirri_tolere_etmeli()
        // Elle girişte 4'lü gruplar boşlukla yazılır — çözümleyici ayraçları yok sayar
        => Totp.Verify("GEZD GNBV GY3T QOJQ GEZD GNBV GY3T QOJQ", "287082",
            DateTimeOffset.FromUnixTimeSeconds(59)).ShouldBeTrue();
}
