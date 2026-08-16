using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.PayTR;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// PayTR adaptörü. <b>Sağlayıcının kabul edeceğini KANITLAMAZ</b> — canlı hesapla
/// doğrulanmadı. Kanıtladıkları: üç ayrı imzanın belgelenen alan listelerine uyması,
/// bildirim imzasının TUTARI kapsaması ve imzasız dönüşün asla kabul edilmemesi.
/// </summary>
public sealed class PayTRTests
{
    private const string Key = "merchant-key-9F3A";
    private const string Salt = "merchant-salt-1";

    private static readonly ConnectorCredentials Kimlik = new(new Dictionary<string, string>
    {
        ["gateway_base"] = "https://www.paytr.test",
        ["merchant_id"] = "M1",
        ["merchant_key"] = Key,
        ["merchant_salt"] = Salt,
    });

    private static string Hmac(string metin)
        => Convert.ToBase64String(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes(metin)));

    [Theory]
    [InlineData(3456, "3456")]     // 34,56 ₺
    [InlineData(14_990, "14990")]  // 149,90 ₺
    public void Tutar_KURUS_tam_sayi_gitmeli(long minor, string beklenen)
    {
        // PayTR "34.56 => 3456" diyor. Noktalı göndermek 100 kat hata riskidir.
        PayTRMessages.Amount(minor).ShouldBe(beklenen);
        PayTRMessages.Amount(minor).ShouldNotContain(".");
    }

    [Fact]
    public void Istek_imzasi_belgelenen_siraya_uymali()
        => PayTRMessages.RequestToken("M1", "1.2.3.4", "att_1", "a@b.c", "3456", "card",
                "0", "TL", "0", "0", Key, Salt)
            .ShouldBe(Hmac("M1" + "1.2.3.4" + "att_1" + "a@b.c" + "3456" + "card" + "0" + "TL" + "0" + "0" + Salt));

    [Fact]
    public void Bildirim_imzasi_belgelenen_siraya_uymali()
        => PayTRMessages.NotificationHash("att_1", "success", "3456", Key, Salt)
            .ShouldBe(Hmac("att_1" + Salt + "success" + "3456"));

    [Fact]
    public void Iade_imzasi_belgelenen_siraya_uymali()
        => PayTRMessages.RefundToken("M1", "att_1", "3456", Key, Salt)
            .ShouldBe(Hmac("M1" + "att_1" + "3456" + Salt));

    [Fact]
    public void Uc_imza_BIRBIRINDEN_farkli_olmali()
    {
        // Tuzun yeri her birinde değişiyor: istekte sonda, bildirimde sipariş
        // numarasından hemen sonra, iadede yine sonda ama farklı alanlarla.
        // Birini diğerinin yerine kullanmak doğrulamanın hep düşmesine yol açar.
        var istek = PayTRMessages.RequestToken("M1", "ip", "att_1", "e", "3456", "card", "0", "TL", "0", "0", Key, Salt);
        var bildirim = PayTRMessages.NotificationHash("att_1", "success", "3456", Key, Salt);
        var iade = PayTRMessages.RefundToken("M1", "att_1", "3456", Key, Salt);

        new[] { istek, bildirim, iade }.ShouldBeUnique();
    }

    [Fact]
    public void Basari_success_DIZESIDIR_kod_degil()
    {
        // Diğer sağlayıcıların çoğunda onay "00" ya da "1"dir; burada dize.
        PayTRMessages.Onaylandi("success").ShouldBeTrue();
        PayTRMessages.Onaylandi("failed").ShouldBeFalse();
        PayTRMessages.Onaylandi("00").ShouldBeFalse();
        PayTRMessages.Onaylandi(null).ShouldBeFalse();
    }

    [Fact]
    public void Imzasiz_bildirim_REDDEDILMELI()
    {
        // Tarayıcının döndüğü merchant_ok_url imzasızdır ve kanıt değildir —
        // burada tam olarak o reddediliyor.
        var sonuc = new PayTRConnector(new BosFabrika()).ParseAndValidateCallback(
            new Dictionary<string, string>
            {
                ["merchant_oid"] = "att_0001",
                ["status"] = "success",
                ["total_amount"] = "3456",
            }, Kimlik);

        sonuc.Success.ShouldBeFalse();
        sonuc.UnifiedCode.ShouldBe(UnifiedErrors.SignatureInvalid);
    }

    [Fact]
    public void Kurcalanan_TUTAR_imzayi_dusurmeli()
    {
        // İmza total_amount'u kapsıyor: 1 ₺ ödeyip 1000 ₺ ödenmiş gösterilemesin diye.
        var form = OnayliBildirim();
        form["total_amount"] = "100000";

        new PayTRConnector(new BosFabrika()).ParseAndValidateCallback(form, Kimlik)
            .UnifiedCode.ShouldBe(UnifiedErrors.SignatureInvalid);
    }

    [Fact]
    public void Imzali_basarili_bildirim_kabul_edilmeli()
    {
        var sonuc = new PayTRConnector(new BosFabrika())
            .ParseAndValidateCallback(OnayliBildirim(), Kimlik);

        sonuc.Success.ShouldBeTrue();
        sonuc.OrderId.ShouldBe("att_0001");
        sonuc.ConnectorTxnId.ShouldBe("PID-7");
    }

    [Fact]
    public async Task Hosted_akis_ACIKCA_desteklenmedigini_soylemeli()
        => (await Should.ThrowAsync<ConnectorConfigurationException>(
                () => new PayTRConnector(new BosFabrika()).InitiateHostedPaymentAsync(
                    new HostedPaymentRequest("att_1", 100, "TRY", 1, "https://cb.test", null, null),
                    Kimlik, default)))
            .Message.ShouldContain("direct");

    [Fact]
    public async Task Iptal_desteklenmedigi_ACIKCA_soylenmeli()
    {
        // PayTR'da ayrı iptal ucu yok. Uydurulmuş bir iptal, para geri gitmediği
        // hâlde "iptal edildi" yazmamıza yol açardı.
        var sonuc = await new PayTRConnector(new BosFabrika()).VoidAsync(
            new ConnectorReference("att_1", "PID-7"), Kimlik, default);

        sonuc.Success.ShouldBeFalse();
        sonuc.UnifiedCode.ShouldBe(UnifiedErrors.NotSupported);
    }

    private static Dictionary<string, string> OnayliBildirim()
    {
        var form = new Dictionary<string, string>
        {
            ["merchant_oid"] = "att_0001",
            ["status"] = "success",
            ["total_amount"] = "3456",
            ["payment_id"] = "PID-7",
        };

        form["hash"] = PayTRMessages.NotificationHash("att_0001", "success", "3456", Key, Salt);
        return form;
    }

    private sealed class BosFabrika : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
