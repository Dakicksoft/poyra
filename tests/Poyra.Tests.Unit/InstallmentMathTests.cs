using Poyra.Modules.Installments.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class InstallmentMathTests
{
    [Theory]
    [InlineData(10_000, 0, 10_000)] // vade farksız
    [InlineData(10_000, 350, 10_350)] // %3,50
    [InlineData(10_000, 1_250, 11_250)] // %12,50
    [InlineData(149_900, 900, 163_391)] // 149900 × 1.09 = 163391
    public void Toplam_vade_farkiyla_dogru_hesaplanmali(long amount, int bps, long expected)
        => InstallmentMath.TotalWithRate(amount, bps).ShouldBe(expected);

    [Fact]
    public void Yarim_kurus_bankaci_yuvarlamasiyla_cozulmeli()
    {
        // 1000 × 1.0025 = 1002.5 → çifte (1002) yuvarlanır
        InstallmentMath.TotalWithRate(1_000, 25).ShouldBe(1_002);
        // 3000 × 1.0025 = 3007.5 → çifte (3008) yuvarlanır
        InstallmentMath.TotalWithRate(3_000, 25).ShouldBe(3_008);
    }

    [Fact]
    public void Aylik_bolusum_toplami_kurusuna_tutmali()
    {
        var (monthly, lastMonth) = InstallmentMath.MonthlySplit(10_000, 3);

        monthly.ShouldBe(3_333);
        lastMonth.ShouldBe(3_334);
        (monthly * 2 + lastMonth).ShouldBe(10_000); // kuruş kaybolmaz (İlke: Money)
    }

    [Fact]
    public void Tek_cekimde_bolusum_tutarin_kendisi_olmali()
    {
        var (monthly, lastMonth) = InstallmentMath.MonthlySplit(14_900, 1);
        monthly.ShouldBe(14_900);
        lastMonth.ShouldBe(14_900);
    }
}
