using Poyra.Connectors.AhlPay;
using Poyra.Connectors.Abstractions;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// AHL Pay adaptörü. <b>Sağlayıcının kabul edeceğini KANITLAMAZ</b> — üstelik istek
/// hash formülü kamuya açık dokümanda YOKTUR ve şu an yer tutucudur. Kanıtladıkları:
/// tutarın kuruş gitmesi, iptal edilmiş işlemin başarı sayılmaması ve dönüşün
/// doğrulanmadan kabul edilmemesi.
/// </summary>
public sealed class AhlPayTests
{
    private static readonly ConnectorCredentials Kimlik = new(new Dictionary<string, string>
    {
        ["gateway_base"] = "https://testahlsanalpos.ahlpay.com.tr",
        ["merchant_id"] = "97163",
        ["member_id"] = "1",
        ["user_code"] = "api@poyra.test",
        ["password"] = "parola",
    });

    [Theory]
    [InlineData(100, "100")]       // 1,00 ₺
    [InlineData(14_990, "14990")]  // 149,90 ₺
    public void Tutar_KURUS_gitmeli(long minor, string beklenen)
    {
        // Sağlayıcı "100" = 1,00 ₺ diyor. Noktalı gönderirsek ("1.00") 100 kat fazla
        // tahsil riski doğar — bu yüzden ayrı bir testle sabitlendi.
        AhlPayMessages.Amount(minor).ShouldBe(beklenen);
    }

    [Theory]
    [InlineData("AUTH", true)]
    [InlineData("SUCCESS", true)]
    [InlineData("VOID", false)]     // iptal edilmiş — sağlayıcının kendi örnek yanıtı
    [InlineData("REFUND", false)]
    [InlineData(null, false)]
    public void Yalniz_tahsilat_durumlari_basari_sayilmali(string? durum, bool beklenen)
    {
        // Sorgu yanıtında isSuccess=true olsa BİLE txnStatus "VOID" olabilir: sağlayıcının
        // örnek yanıtı tam olarak öyle. Yalnız isSuccess'e bakmak iptal edilmiş bir işlemi
        // tahsilat saymak olurdu.
        AhlPayMessages.TahsilEdildi(durum).ShouldBe(beklenen);
    }

    [Fact]
    public void Rastgele_deger_her_cagrida_degismeli()
        => Enumerable.Range(0, 30).Select(_ => AhlPayMessages.Rastgele()).ShouldBeUnique();

    [Fact]
    public void Donus_dogrulanmadan_BASARI_sayilmamali()
    {
        // Dönüşteki responseHash sağlayıcının kendi örneğinde null; doğrulanamayan
        // bir alana dayanmak yerine sonuç PaymentInquiry'den okunur.
        var sonuc = new AhlPayConnector(new BosFabrika()).ParseAndValidateCallback(
            new Dictionary<string, string>
            {
                ["orderId"] = "att_0001",
                ["responseCode"] = "00",
                ["responseHash"] = "",
            }, Kimlik);

        sonuc.Success.ShouldBeFalse();
        sonuc.OrderId.ShouldBe("att_0001");
    }

    [Fact]
    public async Task Hosted_akis_ACIKCA_desteklenmedigini_soylemeli()
        => (await Should.ThrowAsync<ConnectorConfigurationException>(
                () => new AhlPayConnector(new BosFabrika()).InitiateHostedPaymentAsync(
                    new HostedPaymentRequest("att_1", 100, "TRY", 1, "https://cb.test", null, null),
                    Kimlik, default)))
            .Message.ShouldContain("direct");

    private sealed class BosFabrika : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
