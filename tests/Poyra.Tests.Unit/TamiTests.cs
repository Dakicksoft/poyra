using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Tami;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Tami adaptörü. <b>Sağlayıcının kabul edeceğini KANITLAMAZ</b> — canlı hesapla
/// doğrulanmadı. Kanıtladıkları: JWS imzasının belgelenen yapıya uyması (HS512, üç
/// parça, base64url), anahtarın base64url ÇÖZÜLEREK kullanılması ve dönüşün
/// doğrulanmadan başarı sayılmaması.
/// </summary>
public sealed class TamiTests
{
    // JWK'daki k alanı base64url kodludur — testte de öyle verilir.
    private static readonly string JwkKey = Base64Url.EncodeToString("gizli-anahtar-32-baytlik-deger!!"u8.ToArray());

    private static readonly ConnectorCredentials Kimlik = new(new Dictionary<string, string>
    {
        ["gateway_base"] = "https://paymentapi.tami.test",
        ["merchant_number"] = "M1",
        ["terminal_number"] = "T1",
        ["jwk_kid"] = "kid-1",
        ["jwk_key"] = JwkKey,
    });

    [Fact]
    public void Imza_uc_parcali_JWS_olmali()
    {
        var imza = TamiMessages.SecurityHash("""{"orderId":"att_1"}""", "kid-1", JwkKey);

        imza.Split('.').Length.ShouldBe(3);
        imza.ShouldNotContain("="); // base64url dolgusuzdur
        imza.ShouldNotContain("+");
        imza.ShouldNotContain("/");
    }

    [Fact]
    public void Imza_belgelenen_HS512_hesabina_uymali()
    {
        const string govde = """{"orderId":"att_1"}""";
        var imza = TamiMessages.SecurityHash(govde, "kid-1", JwkKey);
        var parcalar = imza.Split('.');

        // Bağımsız doğrulama: imza girdisi "başlık.gövde", anahtar base64url ÇÖZÜLMÜŞ hâli
        var beklenen = Base64Url.EncodeToString(HMACSHA512.HashData(
            Base64Url.DecodeFromChars(JwkKey),
            Encoding.UTF8.GetBytes($"{parcalar[0]}.{parcalar[1]}")));

        parcalar[2].ShouldBe(beklenen);
        Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parcalar[1])).ShouldBe(govde);
    }

    [Fact]
    public void Anahtar_DUZ_METIN_sanilirsa_imza_farkli_cikar()
    {
        // JWK'daki k base64url kodludur. Düz metin sanıp olduğu gibi kullanmak,
        // sessizce yanlış imza üretir ve sağlayıcı isteği reddeder.
        var dogru = TamiMessages.SecurityHash("{}", "kid-1", JwkKey);
        var yanlisAnahtar = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JwkKey));

        TamiMessages.SecurityHash("{}", "kid-1", yanlisAnahtar).ShouldNotBe(dogru);
    }

    [Fact]
    public void Govdenin_her_degisikligi_imzayi_degistirmeli()
    {
        // Tami'de imza gövdenin TAMAMINI kapsar — "hangi alan hash'e giriyor" sorusu yok.
        var temel = TamiMessages.SecurityHash("""{"amount":"149.90"}""", "kid-1", JwkKey);

        TamiMessages.SecurityHash("""{"amount":"1.00"}""", "kid-1", JwkKey).ShouldNotBe(temel);
        TamiMessages.SecurityHash("""{"amount":"149.90"}""", "kid-2", JwkKey).ShouldNotBe(temel);
    }

    [Fact]
    public void Tarayici_donusu_BASARI_sayilmamali()
    {
        // hashedData'nın formülü belgelenmediği için dönüş kanıt değil; tahsilat
        // /payment/query ile sunucudan okunur.
        var sonuc = new TamiConnector(new BosFabrika()).ParseAndValidateCallback(
            new Dictionary<string, string>
            {
                ["orderId"] = "att_0001",
                ["mdStatus"] = "1",
                ["success"] = "true",
            }, Kimlik);

        sonuc.Success.ShouldBeFalse();
        sonuc.OrderId.ShouldBe("att_0001");
    }

    [Fact]
    public async Task Hosted_akis_ACIKCA_desteklenmedigini_soylemeli()
        => (await Should.ThrowAsync<ConnectorConfigurationException>(
                () => new TamiConnector(new BosFabrika()).InitiateHostedPaymentAsync(
                    new HostedPaymentRequest("att_1", 100, "TRY", 1, "https://cb.test", null, null),
                    Kimlik, default)))
            .Message.ShouldContain("direct");

    private sealed class BosFabrika : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
