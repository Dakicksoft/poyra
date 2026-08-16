using Microsoft.Extensions.Configuration;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Vault.Infrastructure;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class CardNumbersTests
{
    // Test kartları: yapısal olarak geçerli (Luhn) örnek numaralar — gerçek kart değildir
    private const string ValidVisa = "4111111111111111";
    private const string ValidMastercard = "5555555555554444";

    [Theory]
    [InlineData(ValidVisa, true)]
    [InlineData(ValidMastercard, true)]
    [InlineData("4111111111111112", false)] // son hane bozuk
    [InlineData("411111", false)] // çok kısa
    [InlineData("4111-1111", false)] // rakam dışı
    public void Luhn_dogrulamasi(string pan, bool expected)
        => CardNumbers.IsValidLuhn(pan).ShouldBe(expected);

    [Fact]
    public void Luhn_null_pani_patlamadan_reddetmeli()
    {
        // FluentValidation aynı özellikteki NotEmpty düşse de Must'ı çalıştırır:
        // burada null'a takılmak, alanı eksik gönderen isteği 400 yerine 500 yapıyordu.
        CardNumbers.IsValidLuhn(null).ShouldBeFalse();
        CardNumbers.IsValidLuhn("").ShouldBeFalse();
    }

    [Fact]
    public void Maskeleme_ilk6_son4_birakmali()
    {
        CardNumbers.Mask(ValidVisa).ShouldBe("411111******1111");
        CardNumbers.Mask(ValidVisa).Length.ShouldBe(ValidVisa.Length);
        CardNumbers.Mask("123").ShouldBe("***"); // kısa girdi tamamen maskelenir
    }

    [Theory]
    [InlineData(ValidVisa, "visa")]
    [InlineData(ValidMastercard, "mastercard")]
    [InlineData("2221000000000009", "mastercard")] // 2-serisi
    [InlineData("9792000000000000", "troy")]
    [InlineData("340000000000009", "amex")]
    [InlineData("6011000000000004", "unknown")]
    public void Marka_tespiti(string pan, string expected)
        => CardNumbers.DetectBrand(pan).ShouldBe(expected);

    [Fact]
    public void CardData_ToString_pan_sizdirmamali()
    {
        var card = new CardData(ValidVisa, 12, 2030, "AYSE YILMAZ", "123");

        var text = card.ToString();
        text.ShouldNotContain(ValidVisa);
        text.ShouldNotContain("123"); // CVV log'a düşmez
        text.ShouldContain("411111******1111");
    }
}

public sealed class VaultCryptoTests
{
    private const string ValidVisa = "4111111111111111";

    private static VaultCrypto Create(byte? keyByte = null)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Poyra:VaultKey"] = Convert.ToBase64String(
                    Enumerable.Repeat(keyByte ?? 1, 32).ToArray()),
            })
            .Build());

    [Fact]
    public void Kart_zarfi_gidis_donusu_cvv_haric_korunmali()
    {
        var crypto = Create();
        var card = new CardData(ValidVisa, 8, 2031, "AYSE YILMAZ", "123");

        var restored = crypto.Unprotect(crypto.Protect(card));

        restored.Pan.ShouldBe(ValidVisa);
        restored.ExpiryMonth.ShouldBe(8);
        restored.ExpiryYear.ShouldBe(2031);
        restored.HolderName.ShouldBe("AYSE YILMAZ");
        restored.Cvv.ShouldBeNull(); // PCI: CVV asla saklanmaz
    }

    [Fact]
    public void Parmak_izi_ayni_kart_icin_sabit_farkli_kart_icin_farkli_olmali()
    {
        var crypto = Create();

        crypto.Fingerprint(ValidVisa).ShouldBe(crypto.Fingerprint(ValidVisa));
        crypto.Fingerprint(ValidVisa).ShouldNotBe(crypto.Fingerprint("5555555555554444"));
        crypto.Fingerprint(ValidVisa).Length.ShouldBe(64); // HMAC-SHA256 hex
        crypto.Fingerprint(ValidVisa).ShouldNotContain("4111"); // PAN geri türetilemez
    }

    [Fact]
    public void Farkli_kasa_anahtari_farkli_parmak_izi_uretmeli()
        => Create(1).Fingerprint(ValidVisa).ShouldNotBe(Create(2).Fingerprint(ValidVisa));

    [Fact]
    public void Eksik_anahtar_acik_hata_vermeli()
    {
        var crypto = new VaultCrypto(new ConfigurationBuilder().Build());
        Should.Throw<InvalidOperationException>(() => crypto.Fingerprint(ValidVisa))
            .Message.ShouldContain("Poyra:VaultKey");
    }
}
