using Poyra.Modules.Recon.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class ReconMathTests
{
    [Theory]
    [InlineData(10_000, 200, 200)] // %2,00 → 200 kuruş
    [InlineData(10_000, 250, 250)]
    [InlineData(22_000, 350, 770)] // %3,50 × 220,00 ₺ = 7,70 ₺
    [InlineData(149_900, 189, 2_833)] // 149900 × 0.0189 = 2833.11 → 2833
    [InlineData(10_000, 0, 0)]
    public void Beklenen_komisyon_dogru_hesaplanmali(long gross, int bps, long expected)
        => ReconMath.ExpectedCommission(gross, bps).ShouldBe(expected);

    [Fact]
    public void Yarim_kurus_bankaci_yuvarlamasiyla_cozulmeli()
    {
        // 1000 × 0.0025 = 2.5 → çifte (2); 3000 × 0.0025 = 7.5 → çifte (8)
        ReconMath.ExpectedCommission(1_000, 25).ShouldBe(2);
        ReconMath.ExpectedCommission(3_000, 25).ShouldBe(8);
    }
}
