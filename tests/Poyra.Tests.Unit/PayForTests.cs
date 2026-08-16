using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.PayFor;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// PayFor (QNB Finansbank) adaptörü.
///
/// <b>Bankanın kabul edeceğini KANITLAMAZ</b> — sertifikasyon testlerinden geçmedi.
/// Kanıtladıkları: hash'lerin belgelenen sıraya ve ASCII kodlamasına uyması, her
/// alanın hash'e girmesi ve imzasız dönüşün asla başarı sayılmaması.
/// </summary>
public sealed class PayForImzaTests
{
    [Fact]
    public void Istek_hashi_belgelenen_siraya_uymali()
    {
        var beklenen = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
            "5" + "att_1" + "149.90" + "https://ok" + "https://fail" + "Auth" + "" + "rnd1" + "sir")));

        PayForMessages.RequestHash("5", "att_1", "149.90", "https://ok", "https://fail",
            "Auth", "", "rnd1", "sir").ShouldBe(beklenen);
    }

    [Fact]
    public void Donus_hashi_belgelenen_siraya_uymali()
    {
        var beklenen = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
            "M1" + "sir" + "att_1" + "AUTH9" + "00" + "1" + "rr" + "U1")));

        PayForMessages.ResponseHash("M1", "sir", "att_1", "AUTH9", "00", "1", "rr", "U1")
            .ShouldBe(beklenen);
    }

    [Fact]
    public void Bos_AuthCode_hashe_BOS_olarak_katilmali()
    {
        // Banka uyarısı: 3DModel'de ödeme henüz gönderilmediği için AuthCode boş gelir.
        // Alanı atlamak (yok saymak) ile boş dize olarak katmak FARKLI hash üretir.
        var beklenen = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
            "M1" + "sir" + "att_1" + "" + "00" + "1" + "rr" + "U1")));

        PayForMessages.ResponseHash("M1", "sir", "att_1", null, "00", "1", "rr", "U1")
            .ShouldBe(beklenen);
    }

    [Fact]
    public void Hash_HER_girdiye_duyarli_olmali()
    {
        var temel = PayForMessages.ResponseHash("M1", "sir", "att_1", "A", "00", "1", "rr", "U1");

        PayForMessages.ResponseHash("M2", "sir", "att_1", "A", "00", "1", "rr", "U1").ShouldNotBe(temel);
        PayForMessages.ResponseHash("M1", "baska", "att_1", "A", "00", "1", "rr", "U1").ShouldNotBe(temel);
        PayForMessages.ResponseHash("M1", "sir", "att_2", "A", "00", "1", "rr", "U1").ShouldNotBe(temel);
        PayForMessages.ResponseHash("M1", "sir", "att_1", "A", "99", "1", "rr", "U1").ShouldNotBe(temel);
        PayForMessages.ResponseHash("M1", "sir", "att_1", "A", "00", "0", "rr", "U1").ShouldNotBe(temel);
        PayForMessages.ResponseHash("M1", "sir", "att_1", "A", "00", "1", "xx", "U1").ShouldNotBe(temel);
    }

    [Theory]
    [InlineData("TRY", "949")]
    [InlineData("USD", "840")]
    public void Para_birimi_ISO_SAYISAL_koda_cevrilmeli(string girdi, string beklenen)
        => PayForMessages.Currency(girdi).ShouldBe(beklenen);

    [Theory]
    [InlineData(14_990, "149.90")]
    [InlineData(5, "0.05")]
    public void Tutar_NOKTA_ondalikli_ve_TAM_iki_haneli_olmali(long minor, string beklenen)
    {
        // Banka üç ondalıklı değeri ("99,500") REDDEDİYOR — iade tutarında kritik.
        PayForMessages.Amount(minor).ShouldBe(beklenen);
        PayForMessages.Amount(minor).Split('.')[1].Length.ShouldBe(2);
    }

    [Fact]
    public void API_yaniti_noktali_virgulle_ayrilmis_cift_listesi_olarak_okunmali()
    {
        // İptal/iade uçları JSON ya da XML değil, "Ad=Deger;Ad=Deger" düz metni döner.
        var alanlar = PayForMessages.Oku(
            "AuthCode=123456;HostRefNum=987;ProcReturnCode=00;TransId=T1;ErrMsg=");

        alanlar["ProcReturnCode"].ShouldBe("00");
        alanlar["TransId"].ShouldBe("T1");
        alanlar["ErrMsg"].ShouldBe("");
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>hata</html>")]
    [InlineData("bozuk;veri")]
    public void Bozuk_yanit_bos_sozluk_dondurmeli(string govde)
    {
        // Banka HTML hata sayfası döndürebilir; susup boş dönmek, yanlış ayrıştırılmış
        // bir "ProcReturnCode=00" üretmekten iyidir (fail closed).
        PayForMessages.Oku(govde).ShouldNotContainKey("ProcReturnCode");
    }
}

public sealed class PayForDonusTests
{
    private static readonly ConnectorCredentials Kimlik = new(new Dictionary<string, string>
    {
        ["gateway_base"] = "https://vpos.qnbfinansbank.test",
        ["mbr_id"] = "5",
        ["merchant_id"] = "M1",
        ["user_code"] = "U1",
        ["user_pass"] = "up",
        ["merchant_pass"] = "sir",
    });

    private static Dictionary<string, string> Donus(string procReturnCode, string threeDStatus, bool imzali)
    {
        var form = new Dictionary<string, string>
        {
            ["OrderId"] = "att_0001",
            ["AuthCode"] = "AUTH9",
            ["ProcReturnCode"] = procReturnCode,
            ["3DStatus"] = threeDStatus,
            ["ResponseRnd"] = "rr",
        };

        form["ResponseHash"] = imzali
            ? PayForMessages.ResponseHash("M1", "sir", "att_0001", "AUTH9", procReturnCode, threeDStatus, "rr", "U1")
            : "sahte-imza";

        return form;
    }

    [Fact]
    public void Imzasiz_onay_donusu_REDDEDILMELI()
    {
        var sonuc = new PayForConnector(new BosFabrika()).ParseAndValidateCallback(Donus("00", "1", imzali: false), Kimlik);

        sonuc.Success.ShouldBeFalse();
        sonuc.UnifiedCode.ShouldBe(UnifiedErrors.SignatureInvalid);
    }

    [Fact]
    public void Imzali_onay_donusu_kabul_edilmeli()
    {
        // Bu ailede dönüş imzalı olduğu için ayrı bir sunucu teyidi gerekmez —
        // imza MerchantPass'i bilmeden üretilemez.
        var sonuc = new PayForConnector(new BosFabrika()).ParseAndValidateCallback(Donus("00", "1", imzali: true), Kimlik);

        sonuc.Success.ShouldBeTrue();
        sonuc.AuthCode.ShouldBe("AUTH9");
        sonuc.UnifiedCode.ShouldBe(UnifiedErrors.None);
    }

    [Theory]
    [InlineData("00", "0", UnifiedErrors.ThreeDsFailed)]     // 3D düştü
    [InlineData("51", "1", UnifiedErrors.InsufficientFunds)] // 3D geçti, banka reddetti
    public void Imza_dogru_olsa_bile_basarisiz_kodlar_eslenmeli(
        string kod, string durum, string beklenen)
    {
        var sonuc = new PayForConnector(new BosFabrika()).ParseAndValidateCallback(Donus(kod, durum, imzali: true), Kimlik);

        sonuc.Success.ShouldBeFalse();
        sonuc.UnifiedCode.ShouldBe(beklenen);
    }

    [Fact]
    public async Task Hosted_akis_DESTEKLENIR_kart_bankada_girilir()
    {
        // Diğer yeni konnektörlerden farkı bu: PayFor gerçek banka-hosted akış sunar,
        // yani PCI kapsamı minimaldir ve hosted rota adayı olabilir.
        var form = await new PayForConnector(new BosFabrika()).InitiateHostedPaymentAsync(
            new HostedPaymentRequest("att_1", 149_00, "TRY", 1, "https://cb.test", "Test", null),
            Kimlik, default);

        form.ActionUrl.ShouldContain("/Gateway/");
        form.Fields["SecureType"].ShouldBe("3DPay");
        form.Fields["InstallmentCount"].ShouldBe(""); // tek çekimde BOŞ, "1" değil
        form.Fields["Hash"].ShouldNotBeNullOrWhiteSpace();

        // Sır forma sızmamalı — tarayıcıda görünür
        form.Fields.Values.ShouldNotContain("sir");
    }

    private sealed class BosFabrika : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
