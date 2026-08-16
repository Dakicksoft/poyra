using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Stripe;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Stripe konnektörü — GERÇEK HTTP üzerinden, sahte bir Stripe sunucusuyla.
/// Sahte sunucu istekleri kaydeder; testler hem gönderdiğimizi hem yorumladığımızı
/// doğrular. Konnektör hataları sessizdir: yanlış tutar birimi ya da eksik
/// idempotency anahtarı ancak canlıda, para hareketiyle fark edilir.
/// </summary>
public sealed class StripeConnectorTests : IAsyncLifetime
{
    private FakeStripe _stripe = null!;
    private StripeConnector _connector = null!;
    private ConnectorCredentials _credentials = null!;

    public async Task InitializeAsync()
    {
        _stripe = new FakeStripe();
        await _stripe.StartAsync();

        var services = new ServiceCollection();
        services.AddHttpClient(StripeConnector.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new RedirectToFakeHandler(_stripe.BaseUrl));
        var provider = services.BuildServiceProvider();

        _connector = new StripeConnector(provider.GetRequiredService<IHttpClientFactory>());
        _credentials = new ConnectorCredentials(new Dictionary<string, string>
        {
            ["secret_key"] = "sk_test_sahte",
            ["statement_descriptor"] = "POYRA MAGAZA",
        });
    }

    public Task DisposeAsync() => _stripe.DisposeAsync().AsTask();

    // ---- Hosted ----------------------------------------------------------------

    [Fact]
    public async Task Checkout_oturumu_acilmali_ve_GET_yonlendirmesi_donmeli()
    {
        _stripe.Respond("/v1/checkout/sessions", HttpStatusCode.OK, """
            {"id":"cs_test_1","url":"https://checkout.stripe.com/c/pay/cs_test_1?x=1#fid=abc"}
            """);

        var form = await _connector.InitiateHostedPaymentAsync(
            new HostedPaymentRequest("att_0001", 14_900, "EUR", 1,
                "https://api.poyra.test/v1/callbacks/stripe/tok", "Koltuk", null),
            _credentials, default);

        // GET yönlendirmesi: adres sorgu dizesi ve parça taşır, forma çevrilirse bozulur
        form.Method.ShouldBe("GET");
        form.Fields.ShouldBeEmpty();
        form.ActionUrl.ShouldBe("https://checkout.stripe.com/c/pay/cs_test_1?x=1#fid=abc");

        var request = _stripe.LastRequest.ShouldNotBeNull();
        var body = request.Decoded;

        body.ShouldContain("mode=payment");
        body.ShouldContain("client_reference_id=att_0001");
        body.ShouldContain("currency]=eur");                       // Stripe küçük harf ister
        body.ShouldContain("unit_amount]=14900");                  // EUR iki ondalıklı → kuruş aynen
        body.ShouldContain("statement_descriptor");
        // Dönüş adresine oturum kimliği yer tutucusu eklenir
        body.ShouldContain("session_id={CHECKOUT_SESSION_ID}");

        // Idempotency: ağ koparsa yeniden deneme ÇİFT ÇEKİM yapmasın
        request.Headers.ShouldContainKey("Idempotency-Key");
        request.Headers["Idempotency-Key"].ShouldBe("att_0001");
        request.Headers["Authorization"].ShouldBe("Bearer sk_test_sahte");
    }

    [Fact]
    public async Task Sifir_ondalikli_para_biriminde_tutar_bolunmeli()
    {
        _stripe.Respond("/v1/checkout/sessions", HttpStatusCode.OK,
            """{"id":"cs_1","url":"https://checkout.stripe.com/x"}""");

        // 10.000 kuruş = 100,00 birim. JPY'de en küçük birim YEN'in kendisidir:
        // 10000 göndermek Japonya'ya 100 KAT fazla fatura keserdi.
        await _connector.InitiateHostedPaymentAsync(
            new HostedPaymentRequest("att_jpy", 10_000, "JPY", 1, "https://x/cb", null, null),
            _credentials, default);

        _stripe.LastRequest!.Decoded.ShouldContain("unit_amount]=100");
        StripeAmount.IsZeroDecimal("JPY").ShouldBeTrue();
        StripeAmount.ToApi(10_000, "EUR").ShouldBe(10_000);
    }

    [Fact]
    public async Task Donus_sonucu_adresten_degil_sunucudan_okunmali()
    {
        _stripe.Respond("/v1/checkout/sessions/cs_ok", HttpStatusCode.OK, """
            {"id":"cs_ok","client_reference_id":"att_0002","payment_status":"paid","payment_intent":"pi_9"}
            """);

        var result = await _connector.CompleteHostedCallbackAsync(
            new Dictionary<string, string> { ["session_id"] = "cs_ok" }, _credentials, default);

        result.Success.ShouldBeTrue();
        result.OrderId.ShouldBe("att_0002");
        result.ConnectorTxnId.ShouldBe("pi_9");

        // Adres satırındaki bilgi TEK BAŞINA kanıt sayılmaz
        var naive = _connector.ParseAndValidateCallback(
            new Dictionary<string, string> { ["session_id"] = "cs_ok" }, _credentials);
        naive.Success.ShouldBeFalse();
        naive.RawMessage.ShouldContain("sunucu doğrulaması");
    }

    [Fact]
    public async Task Odenmemis_oturum_basarisiz_sayilmali()
    {
        _stripe.Respond("/v1/checkout/sessions/cs_no", HttpStatusCode.OK, """
            {"id":"cs_no","client_reference_id":"att_0003","payment_status":"unpaid"}
            """);

        var result = await _connector.CompleteHostedCallbackAsync(
            new Dictionary<string, string> { ["session_id"] = "cs_no" }, _credentials, default);

        result.Success.ShouldBeFalse();
        result.OrderId.ShouldBe("att_0003");
        result.UnifiedCode.ShouldBe(UnifiedErrors.ThreeDsFailed);
    }

    // ---- Hata sınıflandırması --------------------------------------------------

    [Fact]
    public async Task Sunucu_hatasi_erisilemez_sayilmali_musteri_hatasi_sayilmamali()
    {
        // 5xx = sağlayıcı sorunu → rota başka POS'a geçmeli
        _stripe.Respond("/v1/checkout/sessions", HttpStatusCode.BadGateway, "{}");

        await Should.ThrowAsync<ConnectorUnavailableException>(async () =>
            await _connector.InitiateHostedPaymentAsync(
                new HostedPaymentRequest("att_5xx", 1_000, "EUR", 1, "https://x/cb", null, null),
                _credentials, default));
    }

    [Fact]
    public async Task Kart_reddi_birlesik_koda_cevrilmeli()
    {
        _stripe.Respond("/v1/payment_intents", HttpStatusCode.PaymentRequired, """
            {"error":{"type":"card_error","code":"card_declined",
                      "decline_code":"insufficient_funds","message":"Your card has insufficient funds."}}
            """);

        var result = await _connector.AuthorizeDirectAsync(
            new DirectPaymentRequest("att_dec", 5_000, "EUR", 1,
                new CardData("4242424242424242", 12, 2030, "AYSE", "123"), null, null),
            _credentials, default);

        result.ShouldNotBeNull();
        result.Success.ShouldBeFalse();
        // decline_code, code'dan daha ayrıntılıdır ve öncelenir
        result.UnifiedCode.ShouldBe(UnifiedErrors.InsufficientFunds);
        result.MaskedPan.ShouldBe("424242******4242");
    }

    [Theory]
    [InlineData("insufficient_funds", "poyra.insufficient_funds")]
    [InlineData("expired_card", "poyra.expired_card")]
    [InlineData("incorrect_cvc", "poyra.invalid_card")]
    [InlineData("authentication_required", "poyra.three_ds_failed")]
    [InlineData("issuer_not_available", "poyra.issuer_unavailable")]
    [InlineData("card_velocity_exceeded", "poyra.limit_exceeded")]
    [InlineData("lost_card", "poyra.card_declined")]     // müşteriye ayrıntı verilmez
    [InlineData("stolen_card", "poyra.card_declined")]
    [InlineData(null, "poyra.processing_error")]
    public void Hata_sozlugu_eslesmeli(string? code, string expected)
        => StripeErrorMap.ToUnified(code).ShouldBe(expected);

    // ---- Direct / iade / iptal --------------------------------------------------

    [Fact]
    public async Task Direct_odeme_3DS_yonlendirmesi_istememeli()
    {
        _stripe.Respond("/v1/payment_intents", HttpStatusCode.OK,
            """{"id":"pi_ok","status":"succeeded"}""");

        var result = await _connector.AuthorizeDirectAsync(
            new DirectPaymentRequest("att_direct", 7_500, "USD", 1,
                new CardData("4242424242424242", 6, 2029, "AYSE", "123"), null, null),
            _credentials, default);

        result!.Success.ShouldBeTrue();
        result.ConnectorTxnId.ShouldBe("pi_ok");

        var body = _stripe.LastRequest!.Decoded;
        body.ShouldContain("confirm=true");
        // Bu uç 3DS'siz satış içindir: yönlendirme isteseydik müşteri hiçbir yere
        // gitmeden ödeme askıda kalırdı
        body.ShouldContain("allow_redirects]=never");
        body.ShouldContain("card][number]=4242424242424242");
    }

    [Fact]
    public async Task Iade_kismi_tutarla_gitmeli()
    {
        _stripe.Respond("/v1/refunds", HttpStatusCode.OK, """{"id":"re_1"}""");

        var result = await _connector.RefundAsync(
            new ConnectorRefundRequest("att_ref", "pi_9", 2_500, "EUR"), _credentials, default);

        result.Success.ShouldBeTrue();
        result.ConnectorTxnId.ShouldBe("re_1");
        _stripe.LastRequest!.Decoded.ShouldContain("payment_intent=pi_9");
        _stripe.LastRequest.Decoded.ShouldContain("amount=2500");
        // İade anahtarı tutarı içerir: aynı ödemeye iki farklı kısmi iade çakışmasın
        _stripe.LastRequest.Headers["Idempotency-Key"].ShouldBe("att_ref-refund-2500");
    }

    [Fact]
    public async Task Referanssiz_iade_ve_iptal_anlasilir_reddedilmeli()
    {
        (await _connector.RefundAsync(
            new ConnectorRefundRequest("att_x", null, 100, "EUR"), _credentials, default))
            .RawMessage.ShouldContain("payment_intent");

        (await _connector.VoidAsync(
            new ConnectorReference("att_x", null), _credentials, default))
            .RawMessage.ShouldContain("payment_intent");
    }

    [Fact]
    public async Task Yoklama_anahtar_gecersizse_saglıksiz_donmeli()
    {
        _stripe.Respond("/v1/balance", HttpStatusCode.Unauthorized, """
            {"error":{"type":"invalid_request_error","code":"api_key_invalid","message":"Invalid API Key"}}
            """);

        var probe = await _connector.ProbeAsync(_credentials, default);

        probe.ShouldNotBeNull();
        probe.Healthy.ShouldBeFalse();
        probe.Detail.ShouldContain("api_key_invalid");
    }

    [Fact]
    public void Katalog_taksit_desteklemedigini_soylemeli()
    {
        // Stripe'ın taksidi yalnız MX/BR'dedir ve TR banka taksidiyle aynı şey değildir.
        // Yanlış beyan, taksitli işlemin bu hesaba yönlenip reddedilmesine yol açar.
        _connector.Descriptor.SupportsInstallments.ShouldBeFalse();
        _connector.Descriptor.SupportsRefund.ShouldBeTrue();
        _connector.Descriptor.Notes.ShouldContain("taksidi YOKTUR");
    }

    // ---- Sahte Stripe -----------------------------------------------------------

    /// <param name="Body">Ham form gövdesi.</param>
    /// <param name="Decoded">
    /// URL-çözülmüş gövde. Stripe iç içe alanları köşeli parantezle yazar
    /// (line_items[0][price_data][unit_amount]) ve bunlar kaçırılır; testin okunur
    /// kalması için çözülmüş hali tutulur.
    /// </param>
    private sealed record RecordedRequest(
        string Path, string Body, string Decoded, Dictionary<string, string> Headers);

    /// <summary>İstekleri gerçek Stripe adresinden sahte sunucuya yönlendirir.</summary>
    private sealed class RedirectToFakeHandler(string baseUrl) : HttpClientHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(baseUrl.TrimEnd('/') + request.RequestUri!.AbsolutePath);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FakeStripe : IAsyncDisposable
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
                    var rawBody = await reader.ReadToEndAsync();
                    LastRequest = new RecordedRequest(
                        context.Request.Url!.AbsolutePath,
                        rawBody,
                        Uri.UnescapeDataString(rawBody.Replace('+', ' ')),
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
