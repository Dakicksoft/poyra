using Poyra.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// EMVCo Merchant-Presented QR (TR Karekod'un taşıyıcı yapısı). Yanlış CRC ya da kayan
/// uzunluk, bankada "geçersiz karekod" demektir: müşteri kasada telefonu tutar,
/// hiçbir şey olmaz. Bu yüzden yapı standardın yayımlanmış test vektörüyle çivilenir.
/// </summary>
public sealed class EmvQrTests
{
    /// <summary>
    /// EMVCo spesifikasyonunun yayımlanmış örnek yükü. CRC "A13A" ile biter; bizim
    /// hesabımız bunu üretemiyorsa TR Karekod da üretemez.
    /// </summary>
    private const string EmvcoReference =
        "00020101021229300012D156000000000510A93FO3230Q31280012D15600000001030812345678520441115802CN"
        + "5914BEST TRANSPORT6007BEIJING64200002ZH0104最佳运输0202北京540523.7253031565502016233030412"
        + "340603***0708A60086670902ME91320016A0112233449988770708123456786304A13A";

    [Fact]
    public void Referans_yukun_crc_si_dogrulanmali()
        => EmvQr.Verify(EmvcoReference).ShouldBeTrue("EMVCo referans yükü doğrulanmalı");

    [Fact]
    public void Referans_yukun_crc_si_yeniden_uretilmeli()
    {
        var withoutCrc = EmvcoReference[..^8];
        EmvQr.Seal(withoutCrc).ShouldBe(EmvcoReference);
    }

    [Fact]
    public void Tek_karakter_degisince_crc_tutmamali()
    {
        // Kurcalanan tutar sağlamayı bozmalı — yoksa QR'daki rakamı değiştirip
        // 1 ₺'ye 1000 ₺'lik işlem geçirilebilirdi
        var tampered = EmvcoReference.Replace("540523.72", "540513.72");
        EmvQr.Verify(tampered).ShouldBeFalse();
    }

    [Fact]
    public void Alan_iki_haneli_uzunlukla_yazilmali()
    {
        EmvQr.Field("00", "01").ShouldBe("000201");
        EmvQr.Field("59", "POYRA MAGAZA").ShouldBe("5912POYRA MAGAZA");
        // 9 karakterlik değer "09" olarak yazılır — tek hane yazmak tüm yükü kaydırır
        EmvQr.Field("60", "ISTANBUL").ShouldBe("6008ISTANBUL");
    }

    [Fact]
    public void Doksan_dokuzu_asan_deger_reddedilmeli()
    {
        // Kırpmak sessiz bir bozulmadır: banka "geçersiz karekod" der, müşteri kasada bekler
        var ex = Should.Throw<ArgumentException>(() => EmvQr.Field("59", new string('A', 100)));
        ex.Message.ShouldContain("99 karakteri aşamaz");
    }

    [Fact]
    public void Gecersiz_etiket_reddedilmeli()
    {
        Should.Throw<ArgumentException>(() => EmvQr.Field("5", "x"));
        Should.Throw<ArgumentException>(() => EmvQr.Field("ab", "x"));
    }

    [Fact]
    public void Sablon_alt_alanlari_ic_ice_yazmali()
    {
        var template = EmvQr.Template("62", ("01", "FTR-2026-1"), ("05", "MASA4"));

        // 62 = ek veri; içinde 01 fatura no ve 05 referans
        template.ShouldBe("6223" + "0110FTR-2026-1" + "0505MASA4");
        EmvQr.Verify(EmvQr.Seal(template)).ShouldBeTrue();
    }

    [Fact]
    public void Bos_alt_alanlar_sablonu_kirletmemeli()
    {
        EmvQr.Template("62", ("01", null), ("05", "")).ShouldBe("");
        EmvQr.Template("62", ("01", "A"), ("05", null)).ShouldBe("6205" + "0101A");
    }

    [Theory]
    [InlineData(14_900, "149.00")]
    [InlineData(100, "1.00")]
    [InlineData(1, "0.01")]
    [InlineData(1_234_567, "12345.67")]
    public void Tutar_nokta_ondalikli_ve_binlik_ayracsiz_olmali(long minor, string expected)
        // Standart nokta ister; TR biçimi ("12.345,67") yazmak yükü bozar
        => EmvQr.Amount(minor).ShouldBe(expected);

    [Theory]
    [InlineData("Şahin Mobilya", "Sahin Mobilya")]
    [InlineData("ÇİĞDEM GIDA", "CIGDEM GIDA")]
    [InlineData("Öz Güneş Ltd.", "Oz Gunes Ltd.")]
    public void Turkce_harfler_asciye_indirgenmeli(string input, string expected)
        // Bazı POS okuyucuları uzunluğu BAYT sayar; çok baytlı "Ş" yükü kaydırır
        => EmvQr.Ascii(input, 25).ShouldBe(expected);

    [Fact]
    public void Ascii_donusumu_uzunlugu_asmamali()
    {
        EmvQr.Ascii("ÇOK UZUN BİR İŞYERİ UNVANI ANONİM ŞİRKETİ", 25).Length.ShouldBeLessThanOrEqualTo(25);
        // Latin dışı karakter düşer, kalan geçerli ASCII olur
        EmvQr.Ascii("مغازة", 25).ShouldBe("");
    }

    [Fact]
    public void Uretilen_yuk_kendi_dogrulamasindan_gecmeli()
    {
        var payload = EmvQr.Seal(
            EmvQr.Field(EmvQr.TagPayloadFormat, "01")
            + EmvQr.Field(EmvQr.TagInitiationMethod, EmvQr.InitiationDynamic)
            + EmvQr.Field(EmvQr.TagCurrency, "949")
            + EmvQr.Field(EmvQr.TagAmount, EmvQr.Amount(24_990))
            + EmvQr.Field(EmvQr.TagCountry, "TR")
            + EmvQr.Field(EmvQr.TagMerchantName, "POYRA MAGAZA")
            + EmvQr.Field(EmvQr.TagMerchantCity, "ISTANBUL"));

        EmvQr.Verify(payload).ShouldBeTrue();
        payload.ShouldStartWith("000201");
        payload.ShouldContain("5303949");
        payload.ShouldContain("5406249.90");
        payload.Length.ShouldBe(payload.LastIndexOf("6304", StringComparison.Ordinal) + 8);
    }
}
