using Poyra.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Türkçe harf katlama. Bu sınıf bir GÖZDEN GEÇİRME BULGUSUYLA doğdu: e-posta
/// normalleştirmesi <c>ToLowerInvariant()</c> kullanıyordu ve 'İ' ile kaydolan
/// kullanıcı hesabına giremiyordu.
/// </summary>
public sealed class TurkishTextTests
{
    [Fact]
    public void DotNet_gercekten_I_harfini_katlamiyor()
    {
        // Testin dayanağı: bu satır bir gün "İ" yerine "i" dönerse katlama gereksizleşir
        "İ".ToLowerInvariant().ShouldBe("İ");
        "I".ToLowerInvariant().ShouldBe("i"); // bunu invariant zaten doğru yapar
    }

    [Fact]
    public void Buyuk_noktali_I_kucuk_i_ile_eslesmeli()
    {
        // Gerçek senaryo: "İbrahim@ornek.com" ile kaydolan kişi girişte
        // "ibrahim@ornek.com" yazar. Katlama olmadan eşleşme YOK → hesabına giremez.
        TurkishText.NormalizeEmail("İbrahim@ornek.com")
            .ShouldBe(TurkishText.NormalizeEmail("ibrahim@ornek.com"));

        TurkishText.NormalizeEmail("İbrahim@ornek.com").ShouldBe("ibrahim@ornek.com");
    }

    [Fact]
    public void Noktasiz_i_KATLANMAMALI()
    {
        // "ısparta@" ile "isparta@" FARKLI adreslerdir — birini eşleştirmek
        // diğerini yanlışlıkla yakalamak olurdu
        TurkishText.Fold("ısparta@ornek.com")
            .ShouldNotBe(TurkishText.Fold("isparta@ornek.com"));
    }

    [Theory]
    [InlineData("  AYŞE@Ornek.COM  ", "ayşe@ornek.com")]
    [InlineData("İSTANBUL", "istanbul")]
    [InlineData("Kahve Dünyası", "kahve dünyası")]
    [InlineData("ÇĞÖŞÜ", "çğöşü")]
    public void Katlama_kirpip_kucultmeli(string input, string expected)
        => TurkishText.Fold(input).ShouldBe(expected);

    [Fact]
    public void Bos_girdi_null_donmeli()
    {
        TurkishText.FoldOrNull(null).ShouldBeNull();
        TurkishText.FoldOrNull("   ").ShouldBeNull();
        TurkishText.FoldOrNull(" X ").ShouldBe("x");
    }
}
