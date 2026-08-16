using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Moka;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Moka adaptörü. <b>Sağlayıcının kabul edeceğini KANITLAMAZ</b> — canlı hesapla
/// doğrulanmadı. Kanıtladıkları: CheckKey türetmesi, tutar/para birimi biçimi ve
/// en önemlisi <b>imzasız dönüşün başarı sayılmaması</b>.
/// </summary>
public sealed class MokaTests
{
    private static readonly ConnectorCredentials Kimlik = new(new Dictionary<string, string>
    {
        ["gateway_base"] = "https://service.moka.test",
        ["dealer_code"] = "BAYI1",
        ["username"] = "kullanici",
        ["password"] = "parola",
    });

    private static MokaConnector Konnektor() => new(new BosFabrika());

    [Fact]
    public void CheckKey_belgelenen_turetmeyi_izlemeli()
    {
        var beklenen = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("BAYI1MKkullaniciPDparola")));

        MokaMessages.CheckKey("BAYI1", "kullanici", "parola").ShouldBe(beklenen);
    }

    [Fact]
    public void CheckKey_ayraclari_alan_sinirini_korumali()
    {
        // "MK"/"PD" ayraçları olmasa bayi "12"+kullanıcı "34" ile bayi "1"+kullanıcı "234"
        // aynı metni üretir ve iki farklı hesap aynı özete sahip olurdu.
        MokaMessages.CheckKey("12", "34", "p")
            .ShouldNotBe(MokaMessages.CheckKey("1", "234", "p"));
    }

    [Theory]
    [InlineData(14_990, "149.90")]
    [InlineData(5, "0.05")]
    public void Tutar_NOKTA_ondalikli_olmali(long minor, string beklenen)
        => MokaMessages.Amount(minor).ShouldBe(beklenen);

    [Theory]
    [InlineData("TRY", "TL")]   // Moka TRY değil TL ister
    [InlineData("USD", "USD")]
    public void Para_birimi_Mokanin_kisaltmasina_cevrilmeli(string girdi, string beklenen)
        => MokaMessages.Currency(girdi).ShouldBe(beklenen);

    [Fact]
    public void Imzasiz_donus_ASLA_basari_sayilmamali()
    {
        // Moka callback'inde imza yoktur. "resultCode=Success" yazan bir POST'u kabul
        // etmek, callback adresini gören müşterinin bedava sipariş yaratması demekti.
        var sonuc = Konnektor().ParseAndValidateCallback(new Dictionary<string, string>
        {
            ["OtherTrxCode"] = "att_0001",
            ["trxCode"] = "77",
            ["resultCode"] = "Success",
        }, Kimlik);

        sonuc.Success.ShouldBeFalse();
        sonuc.OrderId.ShouldBe("att_0001");
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
