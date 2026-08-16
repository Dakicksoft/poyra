using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Payten;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Payten (MSU) adaptörü — Paratika, VakıfPayS, ZiraatPay.
///
/// <b>Sağlayıcının kabul edeceğini KANITLAMAZ</b> — canlı hesapla doğrulanmadı.
/// Kanıtladıkları: callback imzasının doğru alanlardan hesaplanması, kurcalanan her
/// alanın imzayı düşürmesi ve imzasız/yanlış imzalı dönüşün asla başarı sayılmaması.
/// </summary>
public sealed class PaytenImzaTests
{
    private const string Sir = "secret-key-9F3A";

    private static string Imza(string siparis, string musteri, string oturum, string kod, string rastgele)
        => Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', siparis, musteri, oturum, kod, rastgele, Sir))));

    [Fact]
    public void Dogru_imza_kabul_edilmeli()
        => PaytenMessages.ImzaGecerli(
            Imza("att_1", "m1", "tok", "00", "r1"), "att_1", "m1", "tok", "00", "r1", Sir)
            .ShouldBeTrue();

    [Fact]
    public void Onaltilik_kucuk_harf_ve_base64_gosterimleri_de_kabul_edilmeli()
    {
        // Doküman özetin KODLAMASINI söylemiyor; üç gösterim de AYNI özettir, sırrı
        // bilmeyen hiçbirini üretemez. Sertifikasyonda tek biçime sabitlenecek.
        var ozet = SHA512.HashData(Encoding.UTF8.GetBytes(string.Join('|', "att_1", "m1", "tok", "00", "r1", Sir)));

        PaytenMessages.ImzaGecerli(Convert.ToHexStringLower(ozet), "att_1", "m1", "tok", "00", "r1", Sir).ShouldBeTrue();
        PaytenMessages.ImzaGecerli(Convert.ToBase64String(ozet), "att_1", "m1", "tok", "00", "r1", Sir).ShouldBeTrue();
    }

    [Theory]
    [InlineData("att_BASKA", "m1", "tok", "00", "r1")]  // sipariş değişti
    [InlineData("att_1", "m2", "tok", "00", "r1")]      // müşteri değişti
    [InlineData("att_1", "m1", "baska", "00", "r1")]    // oturum belirteci değişti
    [InlineData("att_1", "m1", "tok", "99", "r1")]      // sonuç kodu değişti
    [InlineData("att_1", "m1", "tok", "00", "r2")]      // rastgele değer değişti
    public void Herhangi_bir_alan_kurcalandiginda_imza_dusmeli(
        string siparis, string musteri, string oturum, string kod, string rastgele)
    {
        // İmza tüm alanları kapsamasaydı, saldırgan kapsanmayanı değiştirip aynı imzayı
        // kullanabilirdi — ör. tutarı ya da sonuç kodunu.
        var gecerli = Imza("att_1", "m1", "tok", "00", "r1");

        PaytenMessages.ImzaGecerli(gecerli, siparis, musteri, oturum, kod, rastgele, Sir).ShouldBeFalse();
    }

    [Fact]
    public void Yanlis_sir_imzayi_dusurmeli()
        => PaytenMessages.ImzaGecerli(
            Imza("att_1", "m1", "tok", "00", "r1"), "att_1", "m1", "tok", "00", "r1", "baska-sir")
            .ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bozuk")]
    public void Imzasiz_ya_da_bozuk_deger_reddedilmeli(string? imza)
        => PaytenMessages.ImzaGecerli(imza, "att_1", "m1", "tok", "00", "r1", Sir).ShouldBeFalse();

    [Theory]
    [InlineData(14_990, "149.90")]
    [InlineData(5, "0.05")]
    public void Tutar_NOKTA_ondalikli_olmali(long minor, string beklenen)
        => PaytenMessages.Amount(minor).ShouldBe(beklenen);
}

public sealed class PaytenDonusTests
{
    private static readonly ConnectorCredentials Kimlik = new(new Dictionary<string, string>
    {
        ["gateway_base"] = "https://entegrasyon.paratika.test/paratika/api/v2",
        ["merchant"] = "M1",
        ["merchant_user"] = "api@poyra.test",
        ["merchant_password"] = "parola",
        ["secret_key"] = "secret-key-9F3A",
    });

    private static PaytenConnector Konnektor() => new(new BosFabrika());

    [Fact]
    public void Imzasiz_onay_donusu_REDDEDILMELI()
    {
        // Referans uygulamalar bu platformda imzayı hiç doğrulamıyor; imzasız
        // "responseCode=00" POST'u kabul etmek bedava sipariş demekti.
        var sonuc = Konnektor().ParseAndValidateCallback(new Dictionary<string, string>
        {
            ["merchantPaymentId"] = "att_0001",
            ["responseCode"] = "00",
            ["responseMsg"] = "Approved",
        }, Kimlik);

        sonuc.Success.ShouldBeFalse();
        sonuc.UnifiedCode.ShouldBe(UnifiedErrors.SignatureInvalid);
    }

    [Fact]
    public void Imza_dogru_olsa_bile_tarayici_donusu_BASARI_sayilmamali()
    {
        var imza = Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', "att_0001", "m1", "tok", "00", "r1", "secret-key-9F3A"))));

        var sonuc = Konnektor().ParseAndValidateCallback(new Dictionary<string, string>
        {
            ["merchantPaymentId"] = "att_0001",
            ["customerId"] = "m1",
            ["sessionToken"] = "tok",
            ["responseCode"] = "00",
            ["random"] = "r1",
            ["sdSha512"] = imza,
        }, Kimlik);

        // Tahsilat QUERYTRANSACTION ile sunucudan okunur; imza yalnız ön filtredir.
        sonuc.Success.ShouldBeFalse();
        sonuc.UnifiedCode.ShouldNotBe(UnifiedErrors.SignatureInvalid);
    }

    [Fact]
    public async Task Hosted_akis_ACIKCA_desteklenmedigini_soylemeli()
        => (await Should.ThrowAsync<ConnectorConfigurationException>(
                () => Konnektor().InitiateHostedPaymentAsync(
                    new HostedPaymentRequest("att_1", 100, "TRY", 1, "https://cb.test", null, null),
                    Kimlik, default)))
            .Message.ShouldContain("direct");

    private sealed class BosFabrika : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
