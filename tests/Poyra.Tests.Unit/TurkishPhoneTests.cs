using Poyra.SharedKernel.Messaging;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Numara normalleştirme ve SMS kredi hesabı. İkisi de sessizce yanlış olabilir:
/// yanlış biçimdeki numaraya SMS gitmez ama sağlayıcı "kabul edildi" der; Türkçe
/// karakterli mesaj 160 sanılıp 70'te bölünür ve işyeri üç kat öder.
/// </summary>
public sealed class TurkishPhoneTests
{
    [Theory]
    [InlineData("0532 123 45 67")]
    [InlineData("+90 532 123 45 67")]
    [InlineData("905321234567")]
    [InlineData("5321234567")]
    [InlineData("(0532) 123-45-67")]
    public void Ayni_numaranin_her_yazimi_ayni_sonuca_gelmeli(string raw)
        => TurkishPhone.ToE164(raw).ShouldBe("+905321234567");

    [Theory]
    [InlineData("0212 123 45 67")] // sabit hat — SMS gitmez
    [InlineData("532 123 45")] // eksik hane
    [InlineData("05321234567890")] // fazla hane
    [InlineData("")]
    [InlineData(null)]
    [InlineData("bilinmiyor")]
    public void Gecersiz_numara_reddedilmeli(string? raw)
    {
        TurkishPhone.ToE164(raw).ShouldBeNull();
        TurkishPhone.IsValid(raw).ShouldBeFalse();
    }

    [Fact]
    public void Sabit_hat_sessizce_kabul_EDILMEMELI()
    {
        // "Gönderildi" deyip gitmemesi, işyerinin müşteriye ulaştığını sanması demektir
        TurkishPhone.IsValid("02121234567").ShouldBeFalse();
        TurkishPhone.IsValid("05321234567").ShouldBeTrue();
    }

    [Fact]
    public void Turkce_karakter_krediyi_ikiye_katlamali()
    {
        var ascii = new string('a', 100);
        var turkish = new string('ş', 100);

        TurkishPhone.SegmentCount(ascii).ShouldBe(1); // 160 sınırı
        TurkishPhone.SegmentCount(turkish).ShouldBe(2); // UCS-2: 70 sınırı
    }

    [Fact]
    public void Tek_turkce_harf_bile_siniri_dusurmeli()
    {
        // 100 karakterlik mesaj tek kredi sanılır; içine bir 'ğ' girince ikiye çıkar
        var almostAscii = new string('a', 99) + "ğ";

        TurkishPhone.SegmentCount(new string('a', 100)).ShouldBe(1);
        TurkishPhone.SegmentCount(almostAscii).ShouldBe(2);
    }

    [Fact]
    public void Sinir_degerleri_dogru_olmali()
    {
        TurkishPhone.SegmentCount(new string('a', 160)).ShouldBe(1);
        TurkishPhone.SegmentCount(new string('a', 161)).ShouldBe(2); // 153'lük parçalar
        TurkishPhone.SegmentCount(new string('ş', 70)).ShouldBe(1);
        TurkishPhone.SegmentCount(new string('ş', 71)).ShouldBe(2); // 67'lik parçalar
        TurkishPhone.SegmentCount("").ShouldBe(0);
    }

    [Fact]
    public void ASCII_indirgeme_krediyi_tek_parcaya_dusurmeli()
    {
        const string body = "Ödemeniz için bağlantı: https://poyra.example/l/abc — teşekkürler.";

        TurkishPhone.SegmentCount(body).ShouldBe(1); // zaten kısa
        var ascii = TurkishPhone.ToAscii(body);
        ascii.ShouldBe("Odemeniz icin baglanti: https://poyra.example/l/abc — tesekkurler.");
        ascii.ShouldNotContain("ğ");
        ascii.ShouldNotContain("ş");
    }

    [Fact]
    public void Euro_isareti_GSM7_sayilmali()
    {
        // ₺ GSM-7'de YOKTUR ama € vardır — tutar yazarken bu fark krediyi değiştirir
        TurkishPhone.SegmentCount(new string('a', 150) + "€").ShouldBe(1);
        TurkishPhone.SegmentCount(new string('a', 150) + "₺").ShouldBe(3); // UCS-2, 67'lik parçalar
    }
}
