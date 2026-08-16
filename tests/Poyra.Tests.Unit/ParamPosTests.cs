using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.ParamPos;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// ParamPos (TurkPos) adaptörü — tek SOAP konnektörümüz.
///
/// <b>Sağlayıcının kabul edeceğini KANITLAMAZ</b> — canlı hesapla doğrulanmadı.
/// Kanıtladıkları: tutarın VİRGÜLLÜ gitmesi, "Sonuc>0 başarıdır" kuralı, hash sırası,
/// SOAP zarfının kaçırma yapması ve dönüşün doğrulanmadan başarı sayılmaması.
/// </summary>
public sealed class ParamPosTests
{
    private static readonly ConnectorCredentials Kimlik = new(new Dictionary<string, string>
    {
        ["gateway_base"] = "https://testposws.param.test/turkpos.ws/service_turkpos_prod.asmx",
        ["client_code"] = "C1",
        ["client_username"] = "u1",
        ["client_password"] = "p1",
        ["guid"] = "GUID-1",
    });

    [Theory]
    [InlineData(14_990, "149,90")]
    [InlineData(100_000, "1000,00")]
    [InlineData(5, "0,05")]
    public void Tutar_VIRGULLU_gitmeli(long minor, string beklenen)
    {
        // Diğer sağlayıcıların neredeyse tamamı NOKTA istiyor; ParamPos virgül istiyor.
        // Karıştırmak ya isteğin reddine ya da yanlış tutara yol açar.
        ParamPosMessages.Amount(minor).ShouldBe(beklenen);
        ParamPosMessages.Amount(minor).ShouldNotContain(".");
    }

    [Fact]
    public void Hash_belgelenen_siraya_uymali()
    {
        var beklenen = Convert.ToBase64String(SHA1.HashData(
            Encoding.UTF8.GetBytes("C1" + "GUID-1" + "1" + "149,90" + "149,90" + "att_1")));

        ParamPosMessages.RequestHash("C1", "GUID-1", "1", "149,90", "149,90", "att_1")
            .ShouldBe(beklenen);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("5", true)]
    [InlineData("0", false)]   // sıfır BAŞARI DEĞİL
    [InlineData("-1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Sonuc_POZITIFSE_basarilidir(string? sonuc, bool beklenen)
    {
        // Çoğu protokolde "0" başarıdır; burada değil. Karıştırmak başarısız işlemi
        // tahsilat saymak olurdu.
        ParamPosMessages.Basarili(sonuc).ShouldBe(beklenen);
    }

    [Fact]
    public void Zarf_XML_kacirmasi_yapmali()
    {
        // Açıklamada bir '&' ya da '<' varsa kaçırılmazsa gövde bozulur ve istek
        // anlaşılmaz bir hatayla reddedilir.
        var zarf = ParamPosMessages.Zarf("TP_WMD_UCD", "G1",
            new Dictionary<string, string> { ["Siparis_Aciklama"] = "Kahve & Çay <özel>" },
            "C1", "u1", "p1");

        zarf.ShouldContain("Kahve &amp; Çay &lt;özel&gt;");
        zarf.ShouldNotContain("Kahve & Çay <özel>");
    }

    [Fact]
    public void Zarf_islem_adini_ve_kimligi_tasimali()
    {
        var zarf = ParamPosMessages.Zarf("TP_WMD_Pay", "G1",
            new Dictionary<string, string> { ["Siparis_ID"] = "att_1" }, "C1", "u1", "p1");

        zarf.ShouldContain("<TP_WMD_Pay xmlns=\"https://turkpos.com.tr/\">");
        zarf.ShouldContain("<CLIENT_CODE>C1</CLIENT_CODE>");
        zarf.ShouldContain("<GUID>G1</GUID>");
        zarf.ShouldContain("<Siparis_ID>att_1</Siparis_ID>");
    }

    [Fact]
    public void Bozuk_XML_bos_sozluk_dondurmeli()
    {
        // Sağlayıcı XML yerine HTML hata sayfası döndürebilir; susup boş dönmek,
        // yanlış ayrıştırılmış bir "başarılı" üretmekten iyidir.
        ParamPosMessages.Oku("<html>hata</html>").ShouldNotContainKey("Sonuc");
        ParamPosMessages.Oku("bozuk").ShouldBeEmpty();
        ParamPosMessages.Oku("").ShouldBeEmpty();
    }

    [Fact]
    public void Tarayici_donusu_BASARI_sayilmamali()
    {
        // Tahsilat TP_WMD_Pay sunucu çağrısıyla kesinleşir.
        var sonuc = new ParamPosConnector(new BosFabrika()).ParseAndValidateCallback(
            new Dictionary<string, string>
            {
                ["orderId"] = "att_0001",
                ["mdStatus"] = "1",
                ["md"] = "MD1",
                ["islemGUID"] = "G9",
            }, Kimlik);

        sonuc.Success.ShouldBeFalse();
        sonuc.OrderId.ShouldBe("att_0001");
    }

    [Fact]
    public async Task Hosted_akis_ACIKCA_desteklenmedigini_soylemeli()
        => (await Should.ThrowAsync<ConnectorConfigurationException>(
                () => new ParamPosConnector(new BosFabrika()).InitiateHostedPaymentAsync(
                    new HostedPaymentRequest("att_1", 100, "TRY", 1, "https://cb.test", null, null),
                    Kimlik, default)))
            .Message.ShouldContain("direct");

    private sealed class BosFabrika : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
