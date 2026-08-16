using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Craftgate;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Craftgate konnektörü — GERÇEK HTTP üzerinden, sahte bir Craftgate sunucusuyla.
///
/// <b>Sağlayıcının kabul edeceğini KANITLAMAZ</b> — canlı hesapla doğrulanmadı.
/// Kanıtladıkları, kendi hatalarımıza karşı: imzanın servis adresini kapsaması,
/// dönüş sorgusunun TARAYICIDAN gelen kimliği kullanmaması, bekleyen iadenin başarı
/// sayılmaması ve çok kalemli ödemede iadenin tahmin yürütmemesi.
/// </summary>
public sealed class CraftgateTests : IAsyncLifetime
{
    private const string ApiKey = "api-key-1";
    private const string SecretKey = "SIR-secret-9F3A";

    private SahteCraftgate _sunucu = null!;
    private CraftgateConnector _konnektor = null!;
    private ConnectorCredentials _kimlik = null!;

    public async Task InitializeAsync()
    {
        _sunucu = new SahteCraftgate();
        await _sunucu.StartAsync();

        var services = new ServiceCollection();
        services.AddHttpClient(CraftgateConnector.HttpClientName);
        var provider = services.BuildServiceProvider();

        _konnektor = new CraftgateConnector(provider.GetRequiredService<IHttpClientFactory>());
        _kimlik = new ConnectorCredentials(new Dictionary<string, string>
        {
            ["gateway_base"] = _sunucu.BaseUrl.TrimEnd('/'),
            ["api_key"] = ApiKey,
            ["secret_key"] = SecretKey,
        });
    }

    public Task DisposeAsync() => _sunucu.DisposeAsync().AsTask();

    // ---- Tutar ve imza ---------------------------------------------------------

    [Theory]
    [InlineData(14_990, 149.90)]
    [InlineData(3456, 34.56)]
    [InlineData(100, 1.00)]
    public void Tutar_kurusa_degil_ONDALIGA_cevrilmeli(long minor, double beklenen)
    {
        // Craftgate tutarı JSON SAYISI ister; kuruş göndermek 100 kat tahsilat demek.
        CraftgateMessages.Price(minor).ShouldBe((decimal)beklenen);
    }

    [Fact]
    public void Imza_belgelenen_sirayi_izlemeli()
        => CraftgateMessages.Imza("https://api.test", "/payment/v1/x", ApiKey, SecretKey, "rnd1", "{}")
            .ShouldBe(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(
                "https://api.test" + "/payment/v1/x" + ApiKey + SecretKey + "rnd1" + "{}"))));

    [Fact]
    public void Imza_SERVIS_ADRESINI_de_kapsamali()
    {
        // Sandbox'ta üretilen imza canlıda geçmez. Bunu bilmeden adresi kimlik
        // bilgilerinden alıp imzayı sabit bir adresle kurmak, tüm isteklerin
        // reddedilmesiyle sonuçlanırdı.
        var sandbox = CraftgateMessages.Imza("https://sandbox-api.test", "/y", ApiKey, SecretKey, "r", "{}");
        var canli = CraftgateMessages.Imza("https://api.test", "/y", ApiKey, SecretKey, "r", "{}");

        sandbox.ShouldNotBe(canli);
    }

    [Fact]
    public void Imza_YOLU_da_kapsamali()
        => CraftgateMessages.Imza("https://api.test", "/payment/v1/refunds", ApiKey, SecretKey, "r", "{}")
            .ShouldNotBe(CraftgateMessages.Imza("https://api.test", "/payment/v1/cards", ApiKey, SecretKey, "r", "{}"));

    // ---- Ortak Ödeme Sayfası ---------------------------------------------------

    [Fact]
    public async Task Ortak_sayfa_GET_yonlendirmesi_ve_token_DURUMDA_donmeli()
    {
        _sunucu.Yanit("/payment/v1/checkout-payments/init", HttpStatusCode.OK, """
            {"token":"cg_tok_1","pageUrl":"https://checkout.craftgate.test/cg_tok_1?x=1"}
            """);

        var form = await _konnektor.InitiateHostedPaymentAsync(
            new HostedPaymentRequest("att_0001", 14_990, "TRY", 1,
                "https://api.poyra.test/v1/callbacks/craftgate/tok", "Koltuk", "203.0.113.7"),
            _kimlik, default);

        // Adres sorgu dizesi taşır; forma çevrilirse bozulur.
        form.Method.ShouldBe("GET");
        form.Fields.ShouldBeEmpty();
        form.ActionUrl.ShouldBe("https://checkout.craftgate.test/cg_tok_1?x=1");

        // Dönüşü sorabilmek için gereken token tarayıcıya emanet edilmez.
        form.ConnectorState.ShouldNotBeNull();
        form.ConnectorState!["poyra_cg_token"].ShouldBe("cg_tok_1");

        var istek = _sunucu.Son("/payment/v1/checkout-payments/init");
        istek.Basliklar["x-api-key"].ShouldBe(ApiKey);
        istek.Basliklar["x-auth-version"].ShouldBe("v1");
        istek.Basliklar["x-signature"].ShouldNotBeNullOrWhiteSpace();
        istek.Basliklar["x-rnd-key"].ShouldNotBeNullOrWhiteSpace();

        // Gizli anahtar imzanın İÇİNDEDİR, başlıkta gitmez.
        istek.Govde.ShouldNotContain(SecretKey);
        istek.Basliklar.Values.ShouldNotContain(SecretKey);

        var govde = JsonDocument.Parse(istek.Govde).RootElement;
        govde.GetProperty("price").GetDecimal().ShouldBe(149.90m);
        govde.GetProperty("conversationId").GetString().ShouldBe("att_0001");
        // Taksit yukarıda karara bağlandı; sayfada tek seçenek açılır ki müşteri
        // başka bir taksite geçip tahsilat tutarını değiştiremesin.
        govde.GetProperty("enabledInstallments").EnumerateArray()
            .Select(t => t.GetInt32()).ShouldBe([1]);
    }

    [Fact]
    public async Task Ortak_sayfa_donusu_DURUMDAKI_tokenle_sorulmali()
    {
        _sunucu.Yanit("/payment/v1/checkout-payments/gercek_token", HttpStatusCode.OK, """
            {"id":501,"paymentStatus":"SUCCESS","conversationId":"att_0001","authCode":"A1",
             "binNumber":"454671","cardIssuerBankName":"Test Bank"}
            """);

        var sonuc = await _konnektor.CompleteHostedCallbackAsync(new Dictionary<string, string>
        {
            ["poyra_cg_token"] = "gercek_token",
            ["token"] = "saldirgan_token", // tarayıcının POST'ladığı — kullanılmamalı
        }, _kimlik, default);

        _sunucu.Yollar.ShouldContain("/payment/v1/checkout-payments/gercek_token");
        _sunucu.Yollar.ShouldNotContain("/payment/v1/checkout-payments/saldirgan_token");

        sonuc.Success.ShouldBeTrue();
        sonuc.ConnectorTxnId.ShouldBe("501"); // iptal/iade bu numarayla yapılır
        sonuc.MaskedPan.ShouldBe("454671******");
    }

    // ---- 3DS'li direct ---------------------------------------------------------

    [Fact]
    public async Task Uc_D_formu_base64_HTML_icinden_cikarilmali()
    {
        var html = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """<form action="https://3d.banka.test/acs"><input name="pareq" value="XYZ" /></form>"""));
        _sunucu.Yanit("/payment/v1/card-payments/3ds-init", HttpStatusCode.OK,
            $$"""{"paymentId":77,"htmlContent":"{{html}}","paymentStatus":"INIT_THREEDS"}""");

        var form = await _konnektor.InitiateThreeDsDirectAsync(
            new DirectPaymentRequest("att_0002", 14_990, "TRY", 1,
                new CardData("4054180000000007", 12, 2030, "POYRA MUSTERI", "123"), "Koltuk", "203.0.113.7"),
            "https://api.poyra.test/v1/callbacks/craftgate/tok", _kimlik, default);

        form.ShouldNotBeNull();
        form!.ActionUrl.ShouldBe("https://3d.banka.test/acs");
        form.Fields["pareq"].ShouldBe("XYZ");

        // Tamamlama çağrısı bu numarayla yapılacak; tarayıcıya emanet edilmiyor.
        form.ConnectorState!["poyra_cg_payment_id"].ShouldBe("77");
    }

    [Fact]
    public async Task Donus_TARAYICININ_verdigi_odeme_numarasini_KULLANMAMALI()
    {
        // Callback birleştirmesinde tarayıcının alanları durumun üzerine yazar.
        // Durum anahtarı bu yüzden "poyra_" önekli: saldırgan paymentId göndererek
        // BAŞKASININ ödemesini tamamlatamasın.
        _sunucu.Yanit("/payment/v1/card-payments/3ds-complete", HttpStatusCode.OK,
            """{"id":77,"paymentStatus":"SUCCESS","conversationId":"att_0002"}""");

        await _konnektor.CompleteHostedCallbackAsync(new Dictionary<string, string>
        {
            ["poyra_cg_payment_id"] = "77",
            ["paymentId"] = "999999",
            ["conversationId"] = "att_0002",
        }, _kimlik, default);

        var govde = JsonDocument.Parse(_sunucu.Son("/payment/v1/card-payments/3ds-complete").Govde).RootElement;
        govde.GetProperty("paymentId").GetInt64().ShouldBe(77);
    }

    [Fact]
    public async Task Sorgu_kimligi_yoksa_ODENDI_denmemeli()
    {
        var sonuc = await _konnektor.CompleteHostedCallbackAsync(new Dictionary<string, string>
        {
            ["conversationId"] = "att_0003",
            ["paymentStatus"] = "SUCCESS", // tarayıcının iddiası
        }, _kimlik, default);

        sonuc.Success.ShouldBeFalse();
        _sunucu.Yollar.ShouldBeEmpty();
    }

    [Fact]
    public void Tarayici_donusu_TEK_BASINA_kabul_edilmemeli()
    {
        var sonuc = _konnektor.ParseAndValidateCallback(new Dictionary<string, string>
        {
            ["conversationId"] = "att_0004",
            ["paymentStatus"] = "SUCCESS",
        }, _kimlik);

        sonuc.Success.ShouldBeFalse();
    }

    // ---- Hata eşlemesi ---------------------------------------------------------

    [Fact]
    public async Task Bakiye_yetersizse_TEKRAR_DENENEBILIR_koda_cevrilmeli()
    {
        _sunucu.Yanit("/payment/v1/checkout-payments/t", HttpStatusCode.OK, """
            {"paymentStatus":"FAILURE","conversationId":"att_0005",
             "paymentError":{"errorCode":"10051","errorGroup":"NOT_SUFFICIENT_FUNDS",
                             "errorDescription":"Yetersiz bakiye"}}
            """);

        var sonuc = await _konnektor.CompleteHostedCallbackAsync(
            new Dictionary<string, string> { ["poyra_cg_token"] = "t" }, _kimlik, default);

        sonuc.Success.ShouldBeFalse();
        sonuc.UnifiedCode.ShouldBe(UnifiedErrors.InsufficientFunds);
        sonuc.RawMessage.ShouldBe("Yetersiz bakiye");
    }

    [Theory]
    [InlineData("LOST_CARD")]
    [InlineData("STOLEN_CARD")]
    [InlineData("PICKUP_CARD")]
    public void Kayip_calinti_kart_YENIDEN_DENENMEMELI(string grup)
    {
        // Bu gruplarda tekrar denemek kart sahibini de bizi de zarara sokar;
        // birleşik kodun failover/dunning tarafında "denenebilir" olmaması şart.
        var kod = CraftgateMessages.UnifiedError(grup, "10041");

        kod.ShouldBe(UnifiedErrors.NotPermitted);
        UnifiedErrors.IsRetryableAtInitiate(kod).ShouldBeFalse();
    }

    [Fact]
    public void Dogrulama_hatasi_KARTA_yikilmamali()
    {
        // Sağlayıcı kuralı: 10000 üstü ödeme hatası, altı doğrulama hatası.
        // Eksik alanı "kart reddedildi" diye göstermek işyerini yanlış yere bakmaya iter.
        CraftgateMessages.UnifiedError(null, "9001").ShouldBe(UnifiedErrors.ProcessingError);
        CraftgateMessages.UnifiedError(null, "10201").ShouldBe(UnifiedErrors.CardDeclined);
    }

    // ---- İptal / iade ----------------------------------------------------------

    [Fact]
    public async Task Iptal_iade_ucundan_gecer_ve_TURU_ham_kodda_kalir()
    {
        // Craftgate'te ayrı iptal ucu yok: gün içi işlemde iade ucu CANCEL üretir.
        // Hangisi olduğu mutabakat için ham kodda taşınır.
        _sunucu.Yanit("/payment/v1/refunds", HttpStatusCode.OK,
            """{"id":900,"status":"SUCCESS","refundType":"CANCEL","paymentId":501}""");

        var sonuc = await _konnektor.VoidAsync(new ConnectorReference("att_0001", "501"), _kimlik, default);

        sonuc.Success.ShouldBeTrue();
        sonuc.RawCode.ShouldBe("CANCEL");
        JsonDocument.Parse(_sunucu.Son("/payment/v1/refunds").Govde)
            .RootElement.GetProperty("paymentId").GetInt64().ShouldBe(501);
    }

    [Fact]
    public async Task Iade_BEKLEMEDE_ise_basari_sayilmamali()
    {
        // WAITING "banka henüz işlemedi" demek. Başarı sayılsaydı, para geri gitmeden
        // iade kapanmış görünür ve kimse peşine düşmezdi.
        _sunucu.Yanit("/payment/v1/refunds", HttpStatusCode.OK,
            """{"id":901,"status":"WAITING","refundType":"REFUND"}""");

        (await _konnektor.VoidAsync(new ConnectorReference("att_0001", "501"), _kimlik, default))
            .Success.ShouldBeFalse();
    }

    [Fact]
    public async Task Kismi_iade_islem_KALEMI_uzerinden_yapilmali()
    {
        _sunucu.Yanit("/payment/v1/card-payments/501", HttpStatusCode.OK,
            """{"id":501,"paymentStatus":"SUCCESS","paymentTransactions":[{"id":55}]}""");
        _sunucu.Yanit("/payment/v1/refund-transactions", HttpStatusCode.OK,
            """{"id":902,"status":"SUCCESS","refundPrice":49.90}""");

        var sonuc = await _konnektor.RefundAsync(
            new ConnectorRefundRequest("att_0001", "501", 4990, "TRY"), _kimlik, default);

        sonuc.Success.ShouldBeTrue();

        var govde = JsonDocument.Parse(_sunucu.Son("/payment/v1/refund-transactions").Govde).RootElement;
        govde.GetProperty("paymentTransactionId").GetInt64().ShouldBe(55);
        govde.GetProperty("refundPrice").GetDecimal().ShouldBe(49.90m);
    }

    [Fact]
    public async Task Cok_kalemli_odemede_iade_TAHMIN_YURUTMEMELI()
    {
        // Tutarın hangi kaleme yazılacağı belirsizken sessizce ilkini seçmek,
        // mutabakatta yeri bulunamayan bir iade üretirdi.
        _sunucu.Yanit("/payment/v1/card-payments/501", HttpStatusCode.OK,
            """{"id":501,"paymentTransactions":[{"id":55},{"id":56}]}""");

        var sonuc = await _konnektor.RefundAsync(
            new ConnectorRefundRequest("att_0001", "501", 4990, "TRY"), _kimlik, default);

        sonuc.Success.ShouldBeFalse();
        _sunucu.Yollar.ShouldNotContain("/payment/v1/refund-transactions");
    }

    [Fact]
    public async Task Odeme_numarasi_yoksa_iade_ISTEK_BILE_gondermemeli()
    {
        var sonuc = await _konnektor.RefundAsync(
            new ConnectorRefundRequest("att_0001", null, 4990, "TRY"), _kimlik, default);

        sonuc.Success.ShouldBeFalse();
        _sunucu.Yollar.ShouldBeEmpty();
    }

    // ---- Sağlayıcı ayakta değilse ----------------------------------------------

    [Fact]
    public async Task Sunucu_5xx_donerse_failover_edilebilir_hata_dogmali()
    {
        _sunucu.Yanit("/payment/v1/checkout-payments/init", HttpStatusCode.BadGateway, "{}");

        await Should.ThrowAsync<ConnectorUnavailableException>(
            () => _konnektor.InitiateHostedPaymentAsync(
                new HostedPaymentRequest("att_0006", 100, "TRY", 1, "https://cb.test", null, null),
                _kimlik, default));
    }

    // ---- Sahte sunucu ----------------------------------------------------------

    private sealed record KayitliIstek(string Yol, string Govde, Dictionary<string, string> Basliklar);

    private sealed class SahteCraftgate : IAsyncDisposable
    {
        private readonly Dictionary<string, (HttpStatusCode Durum, string Govde)> _yanitlar = [];
        private readonly List<KayitliIstek> _istekler = [];
        private HttpListener _dinleyici = null!;

        public string BaseUrl { get; private set; } = "";

        public IReadOnlyList<string> Yollar
        {
            get { lock (_istekler) return [.. _istekler.Select(i => i.Yol)]; }
        }

        public void Yanit(string yol, HttpStatusCode durum, string govde)
            => _yanitlar[yol] = (durum, govde);

        public KayitliIstek Son(string yol)
        {
            lock (_istekler)
                return _istekler.LastOrDefault(i => i.Yol == yol)
                       ?? throw new InvalidOperationException($"'{yol}' hiç çağrılmadı.");
        }

        public Task StartAsync()
        {
            // Port İŞLETİM SİSTEMİNDEN alınır; rastgele seçim paralel testlerde çakışıyordu.
            _dinleyici = new HttpListener();
            BaseUrl = BosPort.Bagla(_dinleyici);

            _ = Task.Run(async () =>
            {
                while (_dinleyici.IsListening)
                {
                    HttpListenerContext baglam;
                    try
                    {
                        baglam = await _dinleyici.GetContextAsync();
                    }
                    catch
                    {
                        return;
                    }

                    using var okuyucu = new StreamReader(baglam.Request.InputStream, Encoding.UTF8);
                    var yol = baglam.Request.Url!.AbsolutePath;
                    var istek = new KayitliIstek(yol, await okuyucu.ReadToEndAsync(),
                        baglam.Request.Headers.AllKeys.Where(k => k is not null)
                            .ToDictionary(k => k!, k => baglam.Request.Headers[k]!));

                    lock (_istekler) _istekler.Add(istek);

                    var (durum, govde) = _yanitlar.GetValueOrDefault(yol, (HttpStatusCode.NotFound, "{}"));
                    baglam.Response.StatusCode = (int)durum;
                    baglam.Response.ContentType = "application/json";
                    await baglam.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(govde));
                    baglam.Response.Close();
                }
            });

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _dinleyici.Stop();
            _dinleyici.Close();
            return ValueTask.CompletedTask;
        }
    }
}
