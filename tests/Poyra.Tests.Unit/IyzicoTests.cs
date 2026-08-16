using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Iyzico;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// İyzico adaptörü.
///
/// <b>Bu testler sağlayıcının kabul edeceğini KANITLAMAZ</b> — canlı hesapla
/// doğrulanmadı. Kanıtladıkları: IYZWSv2 başlığının belgelenen algoritmayı izlemesi,
/// imzanın gövdeye VE yola duyarlı olması, tutar biçimi ve dönüşün doğrulanmadan
/// başarı sayılmaması.
/// </summary>
public sealed class IyzicoImzaTests
{
    private const string ApiKey = "api-key-1";
    private const string SecretKey = "secret-key-1";

    [Fact]
    public void Yetki_basligi_belgelenen_algoritmayi_izlemeli()
    {
        // Bağımsız hesap: HMACSHA256(rastgele + yol + gövde, secret) → onaltılık,
        // sonra "apiKey:…&randomKey:…&signature:…" base64.
        const string rastgele = "abc123";
        const string yol = "/payment/3dsecure/initialize";
        const string govde = """{"price":"149.9"}""";

        var beklenenImza = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SecretKey), Encoding.UTF8.GetBytes(rastgele + yol + govde)));
        var beklenen = "IYZWSv2 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"apiKey:{ApiKey}&randomKey:{rastgele}&signature:{beklenenImza}"));

        IyzicoMessages.YetkiBasligi(ApiKey, SecretKey, yol, govde, rastgele).ShouldBe(beklenen);
    }

    [Fact]
    public void Imza_govdeye_VE_yola_duyarli_olmali()
    {
        // Yol imzaya girmeseydi, bir uç için üretilmiş imza başka uca taşınabilirdi.
        var temel = IyzicoMessages.YetkiBasligi(ApiKey, SecretKey, "/a", "{}", "r1");

        IyzicoMessages.YetkiBasligi(ApiKey, SecretKey, "/b", "{}", "r1").ShouldNotBe(temel);
        IyzicoMessages.YetkiBasligi(ApiKey, SecretKey, "/a", """{"x":1}""", "r1").ShouldNotBe(temel);
        IyzicoMessages.YetkiBasligi(ApiKey, SecretKey, "/a", "{}", "r2").ShouldNotBe(temel);
        IyzicoMessages.YetkiBasligi(ApiKey, "baska-sir", "/a", "{}", "r1").ShouldNotBe(temel);
    }

    [Fact]
    public void Rastgele_anahtar_her_cagrida_degismeli()
    {
        var uretilenler = Enumerable.Range(0, 50).Select(_ => IyzicoMessages.RastgeleAnahtar()).ToList();

        uretilenler.ShouldBeUnique();
        uretilenler.ShouldAllBe(a => a.Length == 24); // 12 bayt → 24 onaltılık karakter
    }

    // İyzico'nun kendi örnekleri "1.0" biçiminde: tam sayıda bile bir ondalık kalır.
    // Sondaki gereksiz sıfırlar atılır ("149.90" → "149.9") ama nokta çıplak kalmaz.
    [Theory]
    [InlineData(14_990, "149.9")]
    [InlineData(100_000, "1000.0")]
    [InlineData(5, "0.05")]
    [InlineData(150_00, "150.0")]
    public void Tutar_NOKTALI_ve_gereksiz_sifirsiz_olmali(long minor, string beklenen)
        => IyzicoMessages.Price(minor).ShouldBe(beklenen);

    [Fact]
    public void Base64_HTML_icinden_form_cikarilmali()
    {
        var html = """
            <html><body><form action="https://3ds.iyzico.test/go" method="post">
            <input type="hidden" name="token" value="tok-1" /></form></body></html>
            """;
        var kodlu = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));

        var form = IyzicoMessages.FormuCoz(kodlu);

        form.ShouldNotBeNull();
        form!.Value.ActionUrl.ShouldBe("https://3ds.iyzico.test/go");
        form.Value.Fields["token"].ShouldBe("tok-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("base64-degil!!")]
    public void Bozuk_icerik_null_donmeli(string? kodlu)
        => IyzicoMessages.FormuCoz(kodlu).ShouldBeNull();
}

public sealed class IyzicoDonusTests
{
    private static readonly ConnectorCredentials Kimlik = new(new Dictionary<string, string>
    {
        ["gateway_base"] = "https://api.iyzipay.test",
        ["api_key"] = "api-key-1",
        ["secret_key"] = "secret-key-1",
    });

    private static IyzicoConnector Konnektor() => new(new BosFabrika());

    [Fact]
    public void Tarayici_donusu_3D_gecse_bile_BASARI_sayilmamali()
    {
        // Tahsilat /payment/3dsecure/auth ile kesinleşir; burada "başarılı" demek
        // parası gelmemiş siparişi ödenmiş göstermek olurdu.
        var sonuc = Konnektor().ParseAndValidateCallback(new Dictionary<string, string>
        {
            ["conversationId"] = "att_0001",
            ["mdStatus"] = "1",
            ["paymentId"] = "12345",
        }, Kimlik);

        sonuc.Success.ShouldBeFalse();
    }

    [Theory]
    [InlineData("0", UnifiedErrors.ThreeDsFailed)]
    [InlineData("2", UnifiedErrors.ThreeDsUnavailable)]
    public void Basarisiz_3D_dogru_koda_eslenmeli(string mdStatus, string beklenen)
        => Konnektor().ParseAndValidateCallback(new Dictionary<string, string>
        {
            ["conversationId"] = "att_0001",
            ["mdStatus"] = mdStatus,
        }, Kimlik).UnifiedCode.ShouldBe(beklenen);

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
