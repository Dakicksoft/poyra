using Poyra.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Form alanına yazılan tutarın kuruşa çevrilmesi. Aynı müşteri aynı tutarı TR yazımıyla
/// ("1.234,56") ya da tarayıcının invariant number input'uyla ("1234.56") gönderebilir;
/// ikisi de aynı kuruşu vermelidir. Yanlış ayrıştırma doğrudan yanlış tahsilat demektir.
/// </summary>
public sealed class UserInputTests
{
    [Theory]
    // Ayraçsız
    [InlineData("100", 10_000)]
    [InlineData("0,5", 50)]
    // TR yazımı: virgül ondalık, nokta binlik
    [InlineData("199,90", 19_990)]
    [InlineData("1.234,56", 123_456)]
    [InlineData("1.234.567,89", 123_456_789)]
    // Invariant (tarayıcı number input'u)
    [InlineData("33.45", 3_345)]
    [InlineData("1,234.56", 123_456)]
    // Tek nokta + tam üç basamak = binlik ayracı (TR); kuruş iki basamaktır
    [InlineData("1.500", 150_000)]
    [InlineData("12.000", 1_200_000)]
    // Kuruş altı KESİLİR — gösterilen tutarla tahsil edilen aynı olmalı
    [InlineData("10,999", 1_099)]
    [InlineData("0,001", 0)]
    // Süs karakterleri ve boşluk
    [InlineData(" 149,00 ₺ ", 14_900)]
    // Geçersiz/boş
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("abc", 0)]
    [InlineData("-50", 0)]
    [InlineData("0", 0)]
    public void ToKurus_tr_ve_invariant_yazimi_ayni_kurusa_cevirmeli(string? input, long expected)
        => UserInput.ToKurus(input).ShouldBe(expected);

    [Fact]
    public void ToDate_bos_metni_null_dondurmeli()
    {
        // Boş bırakılan <input type="date"> boş METİN gönderir — çerçeve bunu 400'e çevirir
        UserInput.ToDate("").ShouldBeNull();
        UserInput.ToDate(null).ShouldBeNull();
        UserInput.ToDate("2026-08-15").ShouldBe(new DateOnly(2026, 8, 15));
    }

    [Fact]
    public void ToInt_gecersiz_girdide_varsayilana_dusmeli()
    {
        UserInput.ToInt("6", 1).ShouldBe(6);
        UserInput.ToInt("", 1).ShouldBe(1);
        UserInput.ToInt(null, 3).ShouldBe(3);
        UserInput.ToInt("altı", 1).ShouldBe(1);
    }
}
