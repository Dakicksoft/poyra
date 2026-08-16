using Poyra.Connectors.Abstractions;
using Poyra.Connectors.CCPayment;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// CCPayment altyapısı (Sipay, QNBPay, Vepara, PayBull, Parolapara, IQmoney, HalkÖde).
///
/// <b>Bu testler sağlayıcının kabul edeceğini KANITLAMAZ</b> — protokol dokümanından
/// değil genel desenden yazıldı. Kanıtladıkları: imzanın gidiş-dönüş tutarlılığı,
/// yanlış sırla çözülememesi, kurcalanmış imzanın reddi, tutar biçimi ve dönüşün
/// doğrulanmadan başarı sayılmaması. Sertifikasyonda alan ADLARI değişebilir; bu testler
/// o değişikliği yaparken neyin bozulduğunu söyler.
/// </summary>
public sealed class CCPaymentImzaTests
{
    private const string Sir = "app-secret-9F3A";

    [Fact]
    public void Imza_cozuldugunde_ayni_metni_vermeli()
    {
        var metin = "149.90|1|TRY|merchant-1|att_0001";

        CCPaymentMessages.Coz(CCPaymentMessages.Imzala(metin, Sir), Sir).ShouldBe(metin);
    }

    [Fact]
    public void Ayni_girdi_HER_SEFERINDE_farkli_imza_uretmeli()
    {
        // IV ve tuz rastgele: imza "yeniden üretip eşitleyerek" doğrulanamaz, çözülerek
        // doğrulanır. Bu test o tasarımı sabitler — biri deterministik hâle getirirse kırılır.
        var a = CCPaymentMessages.Imzala("x|y", Sir);
        var b = CCPaymentMessages.Imzala("x|y", Sir);

        a.ShouldNotBe(b);
        CCPaymentMessages.Coz(a, Sir).ShouldBe(CCPaymentMessages.Coz(b, Sir));
    }

    [Fact]
    public void Yanlis_sirla_cozulememeli()
        => CCPaymentMessages.Coz(CCPaymentMessages.Imzala("x|y", Sir), "baska-sir").ShouldBeNull();

    [Theory]
    [InlineData("")]
    [InlineData("bozuk")]
    [InlineData("iv:tuz")]                       // üç parça değil
    [InlineData("kisa:abcd:Zm9v")]               // IV 16 karakter değil
    [InlineData("0123456789abcdef:abcd:!!!")]    // base64 değil
    public void Bozuk_imza_null_donmeli_istisna_ATMAMALI(string imza)
    {
        // İstisna atsaydı sahte bir dönüş 500'e dönüşür ve gerçek hatadan ayırt edilemezdi.
        CCPaymentMessages.Coz(imza, Sir).ShouldBeNull();
    }

    [Fact]
    public void Imza_egik_cizgi_tasimamali()
    {
        // '/' değeri form alanında ve URL'de bozar; platform __ ile taşır.
        for (var i = 0; i < 40; i++)
            CCPaymentMessages.Imzala($"deneme|{i}", Sir).ShouldNotContain("/");
    }

    [Theory]
    [InlineData(14_990, "149.90")]
    [InlineData(100_000, "1000.00")]
    [InlineData(5, "0.05")]
    public void Tutar_NOKTA_ondalikli_iki_haneli_olmali(long minor, string beklenen)
        => CCPaymentMessages.Amount(minor).ShouldBe(beklenen);

    [Fact]
    public void Yonlendirme_formu_HTML_icinden_cikarilmali()
    {
        var html = """
            <html><body onload="document.f.submit()">
            <form name="f" method="POST" action="https://3ds.saglayici.test/redirect">
              <input type="hidden" name="token" value="abc&amp;123" />
              <input type="hidden" name="orderId" value="att_0001">
            </form></body></html>
            """;

        var form = CCPaymentMessages.FormuCikar(html);

        form.ShouldNotBeNull();
        form!.Value.ActionUrl.ShouldBe("https://3ds.saglayici.test/redirect");
        form.Value.Fields["token"].ShouldBe("abc&123"); // HTML kaçışı çözülür
        form.Value.Fields["orderId"].ShouldBe("att_0001");
    }

    [Fact]
    public void Form_yoksa_null_donmeli()
        => CCPaymentMessages.FormuCikar("<html><body>hata</body></html>").ShouldBeNull();
}

public sealed class CCPaymentDonusTests
{
    private static readonly ConnectorCredentials Kimlik = new(new Dictionary<string, string>
    {
        ["gateway_base"] = "https://saglayici.test/ccpayment",
        ["app_id"] = "app-1",
        ["app_secret"] = "app-secret-9F3A",
        ["merchant_key"] = "merchant-1",
    });

    private static CCPaymentConnector Konnektor() => new(new BosFabrika());

    [Fact]
    public void Imzasi_dogru_olsa_bile_tarayici_donusu_BASARI_sayilmamali()
    {
        // Tahsilat /payment/complete sunucu teyidiyle kesinleşir. Burada "başarılı"
        // demek, parası gelmemiş siparişi ödenmiş göstermek olurdu.
        var form = new Dictionary<string, string>
        {
            ["invoice_id"] = "att_0001",
            ["md_status"] = "1",
            ["order_id"] = "SP-77",
            ["hash_key"] = CCPaymentMessages.Imzala("att_0001|1|TRY", "app-secret-9F3A"),
        };

        Konnektor().ParseAndValidateCallback(form, Kimlik).Success.ShouldBeFalse();
    }

    [Fact]
    public void Baska_islemin_imzasi_kabul_edilmemeli()
    {
        // İmza geçerli ama BAŞKA siparişe ait: taşıma saldırısı böyle yapılır.
        var form = new Dictionary<string, string>
        {
            ["invoice_id"] = "att_0001",
            ["md_status"] = "1",
            ["hash_key"] = CCPaymentMessages.Imzala("att_BASKA|1|TRY", "app-secret-9F3A"),
        };

        var sonuc = Konnektor().ParseAndValidateCallback(form, Kimlik);

        sonuc.Success.ShouldBeFalse();
        sonuc.UnifiedCode.ShouldBe(UnifiedErrors.SignatureInvalid);
    }

    [Fact]
    public async Task Hosted_akis_ACIKCA_desteklenmedigini_soylemeli()
    {
        // Sessizce boş form dönmek, rota motorunun bu hesabı hosted akışta aday
        // sanmasına ve ödemenin orada ölmesine yol açardı.
        var hata = await Should.ThrowAsync<ConnectorConfigurationException>(
            () => Konnektor().InitiateHostedPaymentAsync(
                new HostedPaymentRequest("att_1", 100, "TRY", 1, "https://cb.test", null, null),
                Kimlik, default));

        hata.Message.ShouldContain("direct");
    }

    private sealed class BosFabrika : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
