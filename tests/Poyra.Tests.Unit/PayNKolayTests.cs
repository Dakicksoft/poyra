using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.PayNKolay;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// PayNKolay adaptörü. <b>Sağlayıcının kabul edeceğini KANITLAMAZ</b> — canlı hesapla
/// doğrulanmadı. Kanıtladıkları: iki hash'in belgelenen alan sıralarına uyması,
/// imzanın TUTARI kapsaması ve imzasız dönüşün asla başarı sayılmaması.
/// </summary>
public sealed class PayNKolayTests
{
    private const string Sir = "secret-9F3A";

    private static readonly ConnectorCredentials Kimlik = new(new Dictionary<string, string>
    {
        ["gateway_base"] = "https://paynkolay.test",
        ["sx"] = "SX1",
        ["secret_key"] = Sir,
    });

    private static string Sha512B64(string s)
        => Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public void Istek_hashi_belgelenen_siraya_uymali()
        => PayNKolayMessages.RequestHash("SX1", "att_1", "149.90", "https://ok", "https://fail", "r1", "", Sir)
            .ShouldBe(Sha512B64("SX1|att_1|149.90|https://ok|https://fail|r1||" + Sir));

    [Fact]
    public void Donus_hashi_belgelenen_siraya_uymali()
        => PayNKolayMessages.ResponseHash("SX1", "REF1", "A1", "2", "true", "r9", "1", "149.90", "949", Sir)
            .ShouldBe(Sha512B64("SX1|REF1|A1|2|true|r9|1|149.90|949|" + Sir));

    [Fact]
    public void Basari_kodu_2_olmali_00_degil()
    {
        // Diğer sağlayıcıların çoğunda onay "00"dır; buradaki "2" karıştırılırsa
        // başarılı ödemeler reddedilir (ya da tersi).
        PayNKolayMessages.Onaylandi("2").ShouldBeTrue();
        PayNKolayMessages.Onaylandi("00").ShouldBeFalse();
        PayNKolayMessages.Onaylandi("0").ShouldBeFalse();
    }

    [Fact]
    public void Imzasiz_onay_donusu_REDDEDILMELI()
    {
        var sonuc = new PayNKolayConnector(new BosFabrika()).ParseAndValidateCallback(
            new Dictionary<string, string>
            {
                ["CLIENT_REFERENCE_CODE"] = "att_0001",
                ["RESPONSE_CODE"] = "2",
                ["AUTH_CODE"] = "A1",
            }, Kimlik);

        sonuc.Success.ShouldBeFalse();
        sonuc.UnifiedCode.ShouldBe(UnifiedErrors.SignatureInvalid);
    }

    [Fact]
    public void Kurcalanan_TUTAR_imzayi_dusurmeli()
    {
        // İmza AUTHORIZATION_AMOUNT'u kapsıyor: müşteri 1 ₺ ödeyip 1000 ₺ ödenmiş
        // gibi gösteremesin diye. Kapsamasaydı bu test geçmezdi.
        var form = OnayliDonus();
        form["AUTHORIZATION_AMOUNT"] = "1.00"; // imza 149.90 için üretilmişti

        new PayNKolayConnector(new BosFabrika()).ParseAndValidateCallback(form, Kimlik)
            .UnifiedCode.ShouldBe(UnifiedErrors.SignatureInvalid);
    }

    [Fact]
    public void Imzali_onay_donusu_kabul_edilmeli()
    {
        var sonuc = new PayNKolayConnector(new BosFabrika())
            .ParseAndValidateCallback(OnayliDonus(), Kimlik);

        sonuc.Success.ShouldBeTrue();
        sonuc.AuthCode.ShouldBe("A1");
        sonuc.ConnectorTxnId.ShouldBe("REF1");
    }

    private static Dictionary<string, string> OnayliDonus()
    {
        var form = new Dictionary<string, string>
        {
            ["CLIENT_REFERENCE_CODE"] = "att_0001",
            ["REFERENCE_CODE"] = "REF1",
            ["AUTH_CODE"] = "A1",
            ["RESPONSE_CODE"] = "2",
            ["USE_3D"] = "true",
            ["RND"] = "r9",
            ["INSTALLMENT"] = "1",
            ["AUTHORIZATION_AMOUNT"] = "149.90",
            ["CURRENCY_CODE"] = "949",
        };

        form["hashDataV2"] = PayNKolayMessages.ResponseHash(
            "SX1", "REF1", "A1", "2", "true", "r9", "1", "149.90", "949", Sir);
        return form;
    }

    private sealed class BosFabrika : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
