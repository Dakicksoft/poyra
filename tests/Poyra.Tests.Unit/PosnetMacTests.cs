using Poyra.Connectors.Posnet;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Posnet MAC ve biçim kuralları. Yanlış hesaplanan MAC bankada sessizce reddedilir;
/// yanlış biçimlenen sipariş numarası ise daha kötüdür — çakışırsa İKİ FARKLI işlem
/// aynı numarayla gider. Bu yüzden biçim kuralları burada sabitlenir.
/// TODO(cert): beklenen MAC değerleri YKB sertifikasyon vektörleriyle değiştirilecek.
/// </summary>
public sealed class PosnetMacTests
{
    [Fact]
    public void FirstHash_iki_asamali_zincirin_ilk_halkasi_olmali()
    {
        var first = PosnetMac.FirstHash("encKey123", "67005551");

        // Base64 (hex DEĞİL) — NestPay ver3 ile karıştırılmamalı
        first.ShouldEndWith("=");
        Convert.FromBase64String(first).Length.ShouldBe(32); // SHA-256

        // Deterministik: aynı girdi aynı çıktı
        PosnetMac.FirstHash("encKey123", "67005551").ShouldBe(first);
        PosnetMac.FirstHash("encKey123", "67005552").ShouldNotBe(first);
    }

    [Fact]
    public void Mac_alanlarin_hepsine_duyarli_olmali()
    {
        var firstHash = PosnetMac.FirstHash("k", "t");
        var baseline = PosnetMac.Mac("ORDER000000000000001", "14900", "TL", "6706598320", firstHash);

        PosnetMac.Mac("ORDER000000000000002", "14900", "TL", "6706598320", firstHash).ShouldNotBe(baseline);
        PosnetMac.Mac("ORDER000000000000001", "14901", "TL", "6706598320", firstHash).ShouldNotBe(baseline);
        PosnetMac.Mac("ORDER000000000000001", "14900", "US", "6706598320", firstHash).ShouldNotBe(baseline);
        PosnetMac.Mac("ORDER000000000000001", "14900", "TL", "6706598321", firstHash).ShouldNotBe(baseline);
    }

    [Fact]
    public void Siparis_numarasi_tam_20_karakter_olmali()
    {
        // att_ öneki düşer, 20 karaktere SONDAN kırpılır: Guid v7'nin ayırt edici bitleri
        // sondadır — baştan kırpmak aynı milisaniyedeki denemeleri çakıştırırdı
        var a = PosnetMac.OrderId("att_019fc2140a3273609063c4d8d8e3854e");
        var b = PosnetMac.OrderId("att_019fc2140a3273609063c4d8d8e3854f");

        a.Length.ShouldBe(20);
        b.Length.ShouldBe(20);
        a.ShouldNotBe(b); // son karakter farkı korunur

        // Kısa girdi sola sıfırla doldurulur — banka sabit uzunluk bekler
        PosnetMac.OrderId("att_abc").ShouldBe("00000000000000000abc");
    }

    [Fact]
    public void Tutar_kurus_olarak_ondaliksiz_gitmeli()
        => PosnetMac.Amount(14_900).ShouldBe("14900");

    [Theory]
    [InlineData("TRY", "TL")]
    [InlineData("USD", "US")]
    [InlineData("EUR", "EU")]
    [InlineData("try", "TL")]
    public void Para_birimi_harf_kodu_olmali(string currency, string expected)
        // Posnet ISO sayısal kod (949) DEĞİL, harf kodu kullanır
        => PosnetCurrency.Code(currency).ShouldBe(expected);

    [Fact]
    public void Yanit_maci_dogrulanmali_ve_bos_mac_reddedilmeli()
    {
        const string encKey = "encKey123";
        const string terminalId = "67005551";
        const string merchantId = "6706598320";
        const string orderId = "ORDER000000000000001";

        var valid = PosnetMac.Mac($"1;{orderId}", "14900", "TL", merchantId,
            PosnetMac.FirstHash(encKey, terminalId));
        // Üretimdeki birleştirme sırasıyla aynı sonucu üretmeli
        PosnetMac.ValidateResponse(
            Base64Of($"1;{orderId};14900;TL;{merchantId};{PosnetMac.FirstHash(encKey, terminalId)}"),
            "1", orderId, "14900", "TL", merchantId, encKey, terminalId).ShouldBeTrue();

        PosnetMac.ValidateResponse(null, "1", orderId, "14900", "TL", merchantId, encKey, terminalId)
            .ShouldBeFalse();
        PosnetMac.ValidateResponse("bozuk", "1", orderId, "14900", "TL", merchantId, encKey, terminalId)
            .ShouldBeFalse();

        // Tutar kurcalanırsa MAC tutmaz — saldırgan 1 ₺'ye 1000 ₺'lik işlem geçiremez
        PosnetMac.ValidateResponse(
            Base64Of($"1;{orderId};14900;TL;{merchantId};{PosnetMac.FirstHash(encKey, terminalId)}"),
            "1", orderId, "100", "TL", merchantId, encKey, terminalId).ShouldBeFalse();

        valid.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Posnet_hata_kodlari_birlesik_sozluge_cevrilmeli()
    {
        PosnetErrorMap.ToUnified("0").ShouldBe("");
        PosnetErrorMap.ToUnified("0148").ShouldBe("poyra.insufficient_funds");
        PosnetErrorMap.ToUnified("0054").ShouldBe("poyra.expired_card");
        PosnetErrorMap.ToUnified("0091").ShouldBe("poyra.issuer_unavailable");
        // Kayıp/çalıntı kart müşteriye "reddedildi" olarak döner — ayrıntı sızdırılmaz
        PosnetErrorMap.ToUnified("0041").ShouldBe("poyra.card_declined");
        PosnetErrorMap.ToUnified("bilinmeyen").ShouldBe("poyra.card_declined");
        PosnetErrorMap.ToUnified(null).ShouldBe("poyra.processing_error");
    }

    private static string Base64Of(string value)
        => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));
}
