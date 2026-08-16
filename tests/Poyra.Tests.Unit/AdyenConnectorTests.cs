using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Adyen;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Adyen konnektörü — gerçek HTTP üzerinden, sahte bir Adyen sunucusuyla.
/// Stripe ile aynı amaca hizmet eder ama protokolü farklıdır: JSON gövde, X-API-Key,
/// hesaba özel gateway adresi ve ÜÇ ONDALIKLI para birimleri.
/// </summary>
public sealed class AdyenConnectorTests : IAsyncLifetime
{
    private FakeAdyen _adyen = null!;
    private AdyenConnector _connector = null!;
    private ConnectorCredentials _credentials = null!;

    public async Task InitializeAsync()
    {
        _adyen = new FakeAdyen();
        await _adyen.StartAsync();

        var services = new ServiceCollection();
        services.AddHttpClient(AdyenConnector.HttpClientName);
        var provider = services.BuildServiceProvider();

        _connector = new AdyenConnector(provider.GetRequiredService<IHttpClientFactory>());
        _credentials = new ConnectorCredentials(new Dictionary<string, string>
        {
            // Adyen'de gateway adresi hesaba özeldir — sabit değildir, kimlik alanıdır
            ["gateway_base"] = _adyen.BaseUrl,
            ["api_key"] = "AQE-sahte-anahtar",
            ["merchant_account"] = "PoyraECOM",
        });
    }

    public Task DisposeAsync() => _adyen.DisposeAsync().AsTask();

    // ---- Hosted ----------------------------------------------------------------

    [Fact]
    public async Task Odeme_baglantisi_uretilmeli_ve_kimlik_konnektor_durumunda_saklanmali()
    {
        _adyen.Respond("/v71/paymentLinks", HttpStatusCode.Created, """
            {"id":"PL61C53A8B97E6E1C8","url":"https://test.adyen.link/PL61C53A8B97E6E1C8","status":"active"}
            """);

        var form = await _connector.InitiateHostedPaymentAsync(
            new HostedPaymentRequest("att_0001", 14_900, "EUR", 1,
                "https://api.poyra.test/v1/callbacks/adyen/tok", "Koltuk", null),
            _credentials, default);

        form.Method.ShouldBe("GET");
        form.ActionUrl.ShouldBe("https://test.adyen.link/PL61C53A8B97E6E1C8");
        form.Fields.ShouldBeEmpty();

        // Bağlantı kimliği MÜŞTERİYE değil, konnektör durumuna yazılır: tarayıcıya
        // emanet edilseydi kurcalanıp başka bir işlemin sonucu okutulabilirdi
        form.ConnectorState.ShouldNotBeNull();
        form.ConnectorState["poyra_link_id"].ShouldBe("PL61C53A8B97E6E1C8");

        var request = _adyen.LastRequest.ShouldNotBeNull();
        request.Headers["X-API-Key"].ShouldBe("AQE-sahte-anahtar");
        request.Headers["Idempotency-Key"].ShouldBe("att_0001");

        var body = JsonDocument.Parse(request.Body).RootElement;
        body.GetProperty("merchantAccount").GetString().ShouldBe("PoyraECOM");
        body.GetProperty("reference").GetString().ShouldBe("att_0001");
        body.GetProperty("amount").GetProperty("currency").GetString().ShouldBe("EUR");
        body.GetProperty("amount").GetProperty("value").GetInt64().ShouldBe(14_900);
        body.GetProperty("reusable").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Donus_sonucu_sunucudan_okunmali()
    {
        _adyen.Respond("/v71/paymentLinks/PL1", HttpStatusCode.OK, """
            {"id":"PL1","reference":"att_0002","status":"completed","pspReference":"883."}
            """);

        var result = await _connector.CompleteHostedCallbackAsync(
            new Dictionary<string, string> { ["poyra_link_id"] = "PL1" }, _credentials, default);

        result.Success.ShouldBeTrue();
        result.OrderId.ShouldBe("att_0002");
        result.ConnectorTxnId.ShouldBe("883."); // iade/iptalin anahtarı

        // Adres satırındaki bilgi tek başına kanıt sayılmaz
        _connector.ParseAndValidateCallback(
                new Dictionary<string, string> { ["poyra_link_id"] = "PL1" }, _credentials)
            .Success.ShouldBeFalse();
    }

    [Theory]
    [InlineData("active", "poyra.three_ds_failed")]    // müşteri sayfayı kapatıp döndü
    [InlineData("expired", "poyra.three_ds_timeout")]
    [InlineData("paymentPending", "poyra.three_ds_failed")]
    public async Task Tamamlanmamis_baglanti_tahsilat_sayilmamali(string status, string expected)
    {
        _adyen.Respond("/v71/paymentLinks/PL2", HttpStatusCode.OK,
            $$"""{"id":"PL2","reference":"att_0003","status":"{{status}}"}""");

        var result = await _connector.CompleteHostedCallbackAsync(
            new Dictionary<string, string> { ["poyra_link_id"] = "PL2" }, _credentials, default);

        result.Success.ShouldBeFalse();
        result.UnifiedCode.ShouldBe(expected);
    }

    [Fact]
    public async Task Kimliksiz_donus_anlasilir_reddedilmeli()
    {
        var result = await _connector.CompleteHostedCallbackAsync(
            new Dictionary<string, string>(), _credentials, default);

        result.Success.ShouldBeFalse();
        result.RawMessage.ShouldContain("bağlantı kimliği");
    }

    // ---- Tutar birimi -----------------------------------------------------------

    [Theory]
    [InlineData("EUR", 14_900, 14_900)]   // iki ondalık — kuruş aynen
    [InlineData("JPY", 10_000, 100)]      // sıfır ondalık — 100 kat fazla fatura kesilmesin
    [InlineData("KWD", 10_000, 100_000)]  // üç ondalık — 10 kat eksik fatura kesilmesin
    [InlineData("BHD", 2_500, 25_000)]
    public void Tutar_para_biriminin_ondaligina_gore_cevrilmeli(string currency, long minor, long expected)
    {
        AdyenAmount.ToApi(minor, currency).ShouldBe(expected);
        AdyenAmount.FromApi(expected, currency).ShouldBe(minor);
    }

    [Fact]
    public async Task Uc_ondalikli_para_birimi_istekte_dogru_gitmeli()
    {
        _adyen.Respond("/v71/paymentLinks", HttpStatusCode.Created,
            """{"id":"PL3","url":"https://test.adyen.link/PL3"}""");

        // 100,00 KWD = 10.000 kuruş → Adyen 100000 bekler (üç ondalık)
        await _connector.InitiateHostedPaymentAsync(
            new HostedPaymentRequest("att_kwd", 10_000, "KWD", 1, "https://x/cb", null, null),
            _credentials, default);

        JsonDocument.Parse(_adyen.LastRequest!.Body).RootElement
            .GetProperty("amount").GetProperty("value").GetInt64().ShouldBe(100_000);
    }

    // ---- Hata sınıflandırması ---------------------------------------------------

    [Fact]
    public async Task Sunucu_hatasi_erisilemez_sayilmali()
    {
        _adyen.Respond("/v71/paymentLinks", HttpStatusCode.ServiceUnavailable, "{}");

        await Should.ThrowAsync<ConnectorUnavailableException>(async () =>
            await _connector.InitiateHostedPaymentAsync(
                new HostedPaymentRequest("att_5xx", 1_000, "EUR", 1, "https://x/cb", null, null),
                _credentials, default));
    }

    [Fact]
    public async Task Direct_ret_kodu_birlesik_koda_cevrilmeli()
    {
        _adyen.Respond("/v71/payments", HttpStatusCode.OK, """
            {"resultCode":"Refused","refusalReason":"Not enough balance",
             "refusalReasonCode":"12","pspReference":"883.X"}
            """);

        var result = await _connector.AuthorizeDirectAsync(
            new DirectPaymentRequest("att_dec", 5_000, "EUR", 1,
                new CardData("4111111111111111", 3, 2030, "AYSE", "737"), null, null),
            _credentials, default);

        result.ShouldNotBeNull();
        result.Success.ShouldBeFalse();
        result.UnifiedCode.ShouldBe(UnifiedErrors.InsufficientFunds);
        result.MaskedPan.ShouldBe("411111******1111");
        result.ConnectorTxnId.ShouldBe("883.X");
    }

    [Theory]
    [InlineData("12", "poyra.insufficient_funds")]
    [InlineData("6", "poyra.expired_card")]
    [InlineData("8", "poyra.invalid_card")]
    [InlineData("24", "poyra.invalid_card")]
    [InlineData("11", "poyra.three_ds_failed")]
    [InlineData("4", "poyra.issuer_unavailable")]
    [InlineData("18", "poyra.limit_exceeded")]
    [InlineData("5", "poyra.card_declined")]   // Blocked Card — ayrıntı verilmez
    [InlineData("22", "poyra.card_declined")]  // Fraud — ayrıntı verilmez
    public void Ret_sebep_kodlari_eslesmeli(string refusalCode, string expected)
        => AdyenErrorMap.FromResultCode("Refused", refusalCode).ShouldBe(expected);

    [Fact]
    public void Sebep_kodu_yoksa_sonuc_koduna_dusulmeli()
    {
        AdyenErrorMap.FromResultCode("Refused", null).ShouldBe(UnifiedErrors.CardDeclined);
        AdyenErrorMap.FromResultCode("Cancelled", null).ShouldBe(UnifiedErrors.NotPermitted);
        AdyenErrorMap.FromResultCode("Error", null).ShouldBe(UnifiedErrors.ProcessingError);
        AdyenErrorMap.FromResultCode(null, null).ShouldBe(UnifiedErrors.ProcessingError);
    }

    // ---- İade / iptal / yoklama --------------------------------------------------

    [Fact]
    public async Task Kismi_iade_pspReference_ile_gitmeli()
    {
        _adyen.Respond("/v71/payments/883./refunds", HttpStatusCode.Created,
            """{"pspReference":"884.","status":"received"}""");

        var result = await _connector.RefundAsync(
            new ConnectorRefundRequest("att_ref", "883.", 2_500, "EUR"), _credentials, default);

        result.Success.ShouldBeTrue();
        result.ConnectorTxnId.ShouldBe("884.");

        var body = JsonDocument.Parse(_adyen.LastRequest!.Body).RootElement;
        body.GetProperty("amount").GetProperty("value").GetInt64().ShouldBe(2_500);
        // Kısmi iadeler ayrışsın: aynı ödemeye iki farklı iade çakışmamalı
        _adyen.LastRequest.Headers["Idempotency-Key"].ShouldBe("att_ref-refund-2500");
    }

    [Fact]
    public async Task Referanssiz_iade_ve_iptal_anlasilir_reddedilmeli()
    {
        (await _connector.RefundAsync(
            new ConnectorRefundRequest("att_x", null, 100, "EUR"), _credentials, default))
            .RawMessage.ShouldContain("pspReference");

        (await _connector.VoidAsync(
            new ConnectorReference("att_x", null), _credentials, default))
            .RawMessage.ShouldContain("pspReference");
    }

    [Fact]
    public async Task Yoklama_para_hareketi_yaratmamali()
    {
        _adyen.Respond("/v71/paymentMethods", HttpStatusCode.OK, """{"paymentMethods":[]}""");

        var probe = await _connector.ProbeAsync(_credentials, default);

        probe.ShouldNotBeNull();
        probe.Healthy.ShouldBeTrue();
        _adyen.LastRequest!.Path.ShouldBe("/v71/paymentMethods"); // ödeme ucu DEĞİL
    }

    [Fact]
    public async Task Yoklama_yanlis_anahtarda_saglıksiz_donmeli()
    {
        _adyen.Respond("/v71/paymentMethods", HttpStatusCode.Forbidden, """
            {"status":403,"errorCode":"901","message":"Invalid Merchant Account"}
            """);

        var probe = await _connector.ProbeAsync(_credentials, default);

        probe!.Healthy.ShouldBeFalse();
        probe.Detail.ShouldContain("901");
    }

    [Fact]
    public void Katalog_taksit_desteklemedigini_soylemeli()
    {
        _connector.Descriptor.SupportsInstallments.ShouldBeFalse();
        _connector.Descriptor.Notes.ShouldContain("taksidi YOKTUR");
        // Canlıda adres hesaba özeldir — sabit yazmak canlıya çıkışta patlardı
        _connector.Descriptor.CredentialFields.ShouldContain(f => f.Name == "gateway_base");
    }

    // ---- Sahte Adyen -------------------------------------------------------------

    private sealed record RecordedRequest(string Path, string Body, Dictionary<string, string> Headers);

    private sealed class FakeAdyen : IAsyncDisposable
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = [];
        private HttpListener _listener = null!;
        public string BaseUrl { get; private set; } = "";
        public RecordedRequest? LastRequest { get; private set; }

        public void Respond(string path, HttpStatusCode status, string body)
            => _responses[path] = (status, body);

        public Task StartAsync()
        {
            // Port İŞLETİM SİSTEMİNDEN alınır. Rastgele seçip boş olduğunu varsaymak,
            // paralel koşan testlerde ara sıra çakışıp "address already in use" veriyordu.
            _listener = new HttpListener();
            BaseUrl = BosPort.Bagla(_listener);

            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch
                    {
                        return;
                    }

                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    LastRequest = new RecordedRequest(
                        context.Request.Url!.AbsolutePath,
                        await reader.ReadToEndAsync(),
                        context.Request.Headers.AllKeys
                            .Where(k => k is not null)
                            .ToDictionary(k => k!, k => context.Request.Headers[k]!));

                    var (status, body) = _responses.GetValueOrDefault(
                        context.Request.Url.AbsolutePath, (HttpStatusCode.NotFound, "{}"));

                    context.Response.StatusCode = (int)status;
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }
}
