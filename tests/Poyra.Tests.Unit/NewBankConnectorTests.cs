using Poyra.Connectors.Abstractions;
using Poyra.Connectors.InterVpos;
using Poyra.Connectors.Boa;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Denizbank InterVPOS ve Kuveyt Türk adaptörleri.
///
/// <b>Bu testler bankanın kabul edeceğini KANITLAMAZ</b> — protokol ayrıntıları banka
/// dokümanından değil genel desenden yazıldı. Kanıtladıkları şunlar: tutar biçimi,
/// hash'in belirlenimci ve girdiye duyarlı olması, taksit alanının doğru üretilmesi ve
/// dönüşün doğrulanmadan başarı sayılmaması. Sertifikasyonda alan ADLARI değişebilir;
/// bu testler o değişikliği yaparken neyin bozulduğunu söyler.
/// </summary>
public sealed class InterVposMessageTests
{
    [Theory]
    [InlineData(14_990, "149.90")]
    [InlineData(100_000, "1000.00")]
    [InlineData(5, "0.05")]
    public void Tutar_NOKTA_ondalikli_iki_haneli_olmali(long minor, string expected)
    {
        // TR kültürü virgül üretir ve bu sessizce reddedilen bir isteğe dönüşür
        InterVposMessages.Amount(minor).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, "")]     // tek çekim BOŞ gider, "1" değil
    [InlineData(0, "")]
    [InlineData(6, "6")]
    public void Tek_cekimde_taksit_alani_BOS_olmali(int count, string expected)
    {
        // "1" yazmak bazı bankalarda "1 taksit kampanyası" sayılır ve işlem
        // farklı komisyonla geçer
        InterVposMessages.Installment(count).ShouldBe(expected);
    }

    [Fact]
    public void Hash_belirlenimci_olmali()
    {
        var a = InterVposMessages.RequestHash("S1", "att_1", "10.00", "u", "u", "Auth", "", "r", "p");
        var b = InterVposMessages.RequestHash("S1", "att_1", "10.00", "u", "u", "Auth", "", "r", "p");
        a.ShouldBe(b);
    }

    [Fact]
    public void Hash_HER_girdiye_duyarli_olmali()
    {
        // Bir alan hash'e girmiyorsa saldırgan onu değiştirip aynı hash'i üretebilir.
        // Tutarın hash dışında kalması, 1 ₺'lik işlemi 1000 ₺ göstermek demektir.
        var baseline = InterVposMessages.RequestHash("S1", "att_1", "10.00", "ok", "fail", "Auth", "", "r", "p");

        InterVposMessages.RequestHash("S2", "att_1", "10.00", "ok", "fail", "Auth", "", "r", "p").ShouldNotBe(baseline);
        InterVposMessages.RequestHash("S1", "att_2", "10.00", "ok", "fail", "Auth", "", "r", "p").ShouldNotBe(baseline);
        InterVposMessages.RequestHash("S1", "att_1", "20.00", "ok", "fail", "Auth", "", "r", "p").ShouldNotBe(baseline);
        InterVposMessages.RequestHash("S1", "att_1", "10.00", "ok", "fail", "Auth", "", "r", "X").ShouldNotBe(baseline);
    }

    [Fact]
    public void Onaylanmayan_donus_basari_SAYILMAMALI()
    {
        InterVposMessages.IsApproved(new Dictionary<string, string> { ["Response"] = "Declined" })
            .ShouldBeFalse();
        InterVposMessages.IsApproved(new Dictionary<string, string>()).ShouldBeFalse();
        InterVposMessages.IsApproved(new Dictionary<string, string> { ["Response"] = "Approved" })
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData("51", "poyra.insufficient_funds")]
    [InlineData("54", "poyra.expired_card")]
    [InlineData("99", "poyra.card_declined")]   // bilinmeyen kod TERMİNAL sayılır
    // Kod hiç yoksa işleme hatası. Eskiden burada sözlükte OLMAYAN "poyra.connector_error"
    // yazıyordu; test geçiyordu ama DunningPolicy o kodu tanımadığı için abonelik
    // yeniden denemesi varsayılan dala düşüyordu (KonnektorUyumTests bunu yakaladı).
    [InlineData(null, UnifiedErrors.ProcessingError)]
    public void Hata_kodu_birlesik_sozluge_cevrilmeli(string? raw, string expected)
        => InterVposMessages.UnifiedError(raw).ShouldBe(expected);
}

public sealed class KuveytTurkMessageTests
{
    [Theory]
    [InlineData(14_990, "14990")]
    [InlineData(100_000, "100000")]
    public void Tutar_KURUS_gitmeli(long minor, string expected)
    {
        // NestPay'in "149.90" biçiminin AKSİNE. Karıştırmak 100 kat tutar demektir.
        BoaMessages.Amount(minor).ShouldBe(expected);
    }

    [Fact]
    public void Hash_iki_asamali_ve_parolaya_duyarli_olmali()
    {
        var a = BoaMessages.RequestHash("M1", "att_1", "14990", "ok", "fail", "u", "p1");
        var b = BoaMessages.RequestHash("M1", "att_1", "14990", "ok", "fail", "u", "p2");

        a.ShouldNotBe(b);
        a.ShouldBe(BoaMessages.RequestHash("M1", "att_1", "14990", "ok", "fail", "u", "p1"));
    }

    [Fact]
    public void Sadece_00_kodu_basari_olmali()
    {
        BoaMessages.IsApproved(new Dictionary<string, string> { ["ResponseCode"] = "00" })
            .ShouldBeTrue();
        BoaMessages.IsApproved(new Dictionary<string, string> { ["ResponseCode"] = "01" })
            .ShouldBeFalse();
        BoaMessages.IsApproved(new Dictionary<string, string>()).ShouldBeFalse();
    }
}

/// <summary>
/// Yeni adaptörlerin ürün sözleşmesi. En önemlisi: <b>desteklemediğini
/// desteklemiyor demeleri</b>.
/// </summary>
public sealed class NewConnectorContractTests
{
    [Fact]
    public void Iade_desteklenmiyorsa_ACIKCA_soylenmeli()
    {
        // Uydurulmuş bir iade çağrısı, parayı geri göndermediğimiz hâlde
        // "iade edildi" yazmamıza yol açardı. Descriptor false diyorsa rota motoru
        // bu hesabı iade gereken akışa yönlendirmez.
        //
        // Artık dördü de destekliyor; kural şu: DESTEKLENMEYEN bir işlem descriptor'da
        // false demeli ve çağrıldığında NotSupported dönmeli — sessizce "başarılı"
        // dönmemeli. Aşağıdaki ikinci test o sözleşmeyi koruyor.
        new InterVposConnector(BosHttpFabrika.Ornek).Descriptor.SupportsRefund.ShouldBeTrue();
        new KuveytTurkConnector(BosHttpFabrika.Ornek).Descriptor.SupportsRefund.ShouldBeTrue();
        new VakifKatilimConnector(BosHttpFabrika.Ornek).Descriptor.SupportsVoid.ShouldBeTrue();
    }

    [Fact]
    public async Task Eksik_kimlik_alani_BASARILI_donmemeli()
    {
        // İptal/iade artık destekleniyor ama kimlik alanları eksikse sessizce
        // "başarılı" dönmemeli — yapılandırma hatası açıkça söylenmeli.
        await Should.ThrowAsync<ConnectorConfigurationException>(
            () => new InterVposConnector(BosHttpFabrika.Ornek).RefundAsync(
                new ConnectorRefundRequest("att_1", null, 1000, "TRY"),
                new ConnectorCredentials(new Dictionary<string, string>()), default));
    }

    [Fact]
    public void Sertifikasyon_durumu_katalogda_GORUNMELI()
    {
        // İşyeri "bu hazır" sanıp canlıya almasın: sertifikasyon beklediği
        // adının kendisinde yazılı
        new InterVposConnector(BosHttpFabrika.Ornek).Descriptor.DisplayName.ShouldContain("SERTİFİKASYON");
        new KuveytTurkConnector(BosHttpFabrika.Ornek).Descriptor.DisplayName.ShouldContain("SERTİFİKASYON");
        new InterVposConnector(BosHttpFabrika.Ornek).Descriptor.Notes.ShouldContain("TODO(cert)");
    }
}

/// <summary>
/// Kuveyt Türk konnektörü provizyon çağrısı için IHttpClientFactory alır; tanımlayıcı
/// testleri ağa çıkmadığından boş bir fabrika yeterli.
/// </summary>
file sealed class BosHttpFabrika : IHttpClientFactory
{
    public static readonly BosHttpFabrika Ornek = new();

    public HttpClient CreateClient(string name) => new();
}

/// <summary>
/// BOA ailesinin iptal/iade mesajları — Vakıf Katılım'ın kendi entegrasyon dokümanından
/// (v2.7) yazıldı. Kuveyt Türk aynı platformu kullandığı için aynı uçlara gider;
/// bu, sertifikasyonda teyit edilecek bir çıkarımdır (TODO(cert)).
/// </summary>
public sealed class BoaIptalIadeTests
{
    [Fact]
    public void Iptal_mesaji_belgelenen_alanlari_tasimali()
    {
        var xml = BoaMessages.IptalXml("VPosMessageContract", "1", "936", "APIUSER",
            "hashli", "1061162073", "12345", "970", "IMZA");

        xml.ShouldContain("<VPosMessageContract");
        xml.ShouldContain("<HashData>IMZA</HashData>");
        xml.ShouldContain("<MerchantOrderId>1061162073</MerchantOrderId>");
        xml.ShouldContain("<OrderId>12345</OrderId>");
        xml.ShouldContain("<Amount>970</Amount>");
        xml.ShouldContain("<PaymentType>1</PaymentType>");
    }

    [Fact]
    public void Kismi_iade_mesaji_tutari_IKI_alanda_tasimali()
    {
        // Banka hem Amount hem DisplayAmount bekliyor; birini atlamak isteği düşürür.
        var xml = BoaMessages.KismiIadeXml("VPosMessageContract", "1", "11111", "APIUSER",
            "hashli", "1771489024", "15184", "500", "IMZA");

        xml.ShouldContain("<Amount>500</Amount>");
        xml.ShouldContain("<DisplayAmount>500</DisplayAmount>");
        xml.ShouldContain("<OrderId>15184</OrderId>");
    }

    [Fact]
    public void Mesajlar_XML_kacirmasi_yapmali()
    {
        // Kullanıcı adında bir '&' kaçırılmazsa gövde bozulur ve banka isteği reddeder.
        BoaMessages.IptalXml("VPosMessageContract", "1", "9", "API&USER", "h", "o", "1", "0", "IMZA")
            .ShouldContain("API&amp;USER");
    }

    [Fact]
    public void Kok_eleman_bankaya_gore_degismeli()
    {
        // Kuveyt Türk ve Vakıf Katılım aynı ailede ama XML kök elemanları farklı.
        BoaMessages.IptalXml("KuveytTurkVPosMessage", "1", "9", "u", "h", "o", "1", "0", "I")
            .ShouldContain("<KuveytTurkVPosMessage");
        BoaMessages.IptalXml("VPosMessageContract", "1", "9", "u", "h", "o", "1", "0", "I")
            .ShouldContain("<VPosMessageContract");
    }
}
