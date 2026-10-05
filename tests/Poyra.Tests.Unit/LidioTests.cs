using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Lidio;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Lidio konnektörü — GERÇEK HTTP üzerinden, sahte bir Lidio sunucusuyla.
///
/// <b>Sağlayıcının kabul edeceğini KANITLAMAZ</b> — canlı hesapla doğrulanmadı.
/// Kanıtladıkları, kendi hatalarımıza karşı: dönüşün tarayıcı parametreleriyle değil
/// sunucu çağrısıyla kesinleşmesi, sorgu kimliklerinin TARAYICIDAN alınmaması, tutarın
/// karşılaştırılması ve Lidio sonuç kategorilerinin yeniden tahsilatı bozmadan eşlenmesi.
/// </summary>
public sealed class LidioTests : IAsyncLifetime
{
    private const string ApiKey = "SIR-lidio-api-9F3A";
    private const string MerchantCode = "POYRA_TEST";

    private FakeLidio _server = null!;
    private LidioConnector _connector = null!;
    private ConnectorCredentials _credentials = null!;

    public async Task InitializeAsync()
    {
        _server = new FakeLidio();
        await _server.StartAsync();

        var services = new ServiceCollection();
        services.AddHttpClient(LidioConnector.HttpClientName);
        var provider = services.BuildServiceProvider();

        _connector = new LidioConnector(provider.GetRequiredService<IHttpClientFactory>());
        _credentials = new ConnectorCredentials(new Dictionary<string, string>
        {
            // Test ortamı adresi "/api" önekli — uç yolları bunun ARDINA eklenmeli.
            ["gateway_base"] = _server.BaseUrl + "/api/",
            ["merchant_code"] = MerchantCode,
            ["api_key"] = ApiKey,
        });
    }

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    // ---- Biçimler --------------------------------------------------------------

    [Theory]
    [InlineData(14_990, 149.90)]
    [InlineData(3456, 34.56)]
    [InlineData(100, 1.00)]
    public void Amount_is_sent_as_decimal_not_minor_units(long minor, double expected)
        => LidioMessages.Amount(minor).ShouldBe((decimal)expected);

    [Fact]
    public void Server_key_uses_MxS2S_prefix()
        // MxC2S tarayıcıya verilen süreli anahtarın önekidir; karıştırılırsa her istek reddedilir.
        => LidioMessages.Authorization("abc").ShouldBe("MxS2S abc");

    [Fact]
    public void Unsupported_currency_is_rejected_before_any_request()
        => Should.Throw<ConnectorConfigurationException>(() => LidioMessages.Currency("XYZ"));

    // ---- Hosted ödeme sayfası --------------------------------------------------

    [Fact]
    public async Task Hosted_returns_GET_redirect_and_keeps_query_ids_in_state()
    {
        _server.Respond("/api/StartHostedPaymentProcess", HttpStatusCode.OK, """
            {"result":"Success","systemTransId":"ST-1","orderId":"att_0001",
             "redirectURL":"https://pay.lidio.test/h/abc?x=1"}
            """);

        var form = await _connector.InitiateHostedPaymentAsync(
            new HostedPaymentRequest("att_0001", 14_990, "TRY", 1,
                "https://api.poyra.test/v1/callbacks/lidio/tok", "Koltuk", "203.0.113.7"),
            _credentials, default);

        // Adres sorgu dizesi taşır; forma çevrilirse bozulur.
        form.Method.ShouldBe("GET");
        form.Fields.ShouldBeEmpty();
        form.ActionUrl.ShouldBe("https://pay.lidio.test/h/abc?x=1");

        form.ConnectorState.ShouldNotBeNull();
        form.ConnectorState!["poyra_lidio_order_id"].ShouldBe("att_0001");
        form.ConnectorState["poyra_lidio_system_trans_id"].ShouldBe("ST-1");
        form.ConnectorState["poyra_lidio_amount"].ShouldBe("14990");

        var request = _server.Last("/api/StartHostedPaymentProcess");
        request.Headers["Authorization"].ShouldBe("MxS2S " + ApiKey);
        request.Headers["MerchantCode"].ShouldBe(MerchantCode);
        request.Body.ShouldNotContain(ApiKey);

        var body = JsonDocument.Parse(request.Body).RootElement;
        body.GetProperty("totalAmount").GetDecimal().ShouldBe(149.90m);
        body.GetProperty("currency").GetString().ShouldBe("TRY");
        body.GetProperty("returnUrl").GetString().ShouldBe("https://api.poyra.test/v1/callbacks/lidio/tok");
        body.GetProperty("paymentInstruments").EnumerateArray().Select(a => a.GetString())
            .ShouldBe(["NewCard"]);
        body.TryGetProperty("customParameters", out _).ShouldBeFalse(); // tek çekimde taksit sabitlenmez
    }

    [Fact]
    public async Task Installment_count_is_locked_on_hosted_page()
    {
        _server.Respond("/api/StartHostedPaymentProcess", HttpStatusCode.OK,
            """{"result":"Success","systemTransId":"ST-2","redirectURL":"https://pay.lidio.test/h/2"}""");

        await _connector.InitiateHostedPaymentAsync(
            new HostedPaymentRequest("att_0002", 30_000, "TRY", 3, "https://cb.test", null, null),
            _credentials, default);

        // Taksit yukarıda karara bağlandı; müşteri sayfada başka taksite geçip
        // vade farkını (tahsilat tutarını) değiştirememeli. Belge gereği
        // SelectedInstallmentCount kullanılırken useInstallment false olmalı.
        var body = JsonDocument.Parse(_server.Last("/api/StartHostedPaymentProcess").Body).RootElement;
        body.GetProperty("customParameters").GetString().ShouldBe("SelectedInstallmentCount:3");
        body.GetProperty("paymentInstrumentInfo").GetProperty("card")
            .GetProperty("useInstallment").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Page_start_failure_raises_failover_eligible_error()
    {
        _server.Respond("/api/StartHostedPaymentProcess", HttpStatusCode.OK,
            """{"result":"InvalidCredential","resultMessage":"IP tanımlı değil"}""");

        (await Should.ThrowAsync<ConnectorUnavailableException>(
                () => _connector.InitiateHostedPaymentAsync(
                    new HostedPaymentRequest("att_0003", 100, "TRY", 1, "https://cb.test", null, null),
                    _credentials, default)))
            .Message.ShouldContain("InvalidCredential");
    }

    [Fact]
    public async Task Hosted_return_is_queried_with_ids_from_state()
    {
        _server.Respond("/api/GetHostedPaymentStatus", HttpStatusCode.OK, SucceededHostedStatus("att_0001", 149.90m));

        var result = await _connector.CompleteHostedCallbackAsync(new Dictionary<string, string>
        {
            ["poyra_lidio_flow"] = "hosted",
            ["poyra_lidio_order_id"] = "att_0001",
            ["poyra_lidio_system_trans_id"] = "ST-1",
            ["poyra_lidio_amount"] = "14990",
            // Tarayıcının sorgu dizesi — sorguda kullanılmamalı.
            ["OrderId"] = "att_baskasi",
            ["SystemTransId"] = "ST-SALDIRGAN",
            ["Result"] = "Success",
        }, _credentials, default);

        var body = JsonDocument.Parse(_server.Last("/api/GetHostedPaymentStatus").Body).RootElement;
        body.GetProperty("orderId").GetString().ShouldBe("att_0001");
        body.GetProperty("systemTransId").GetString().ShouldBe("ST-1");

        result.Success.ShouldBeTrue();
        result.OrderId.ShouldBe("att_0001");
        result.AuthCode.ShouldBe("A12345");
        result.MaskedPan.ShouldBe("454671******7894");
        result.CardBank.ShouldBe("Test Bankası");
        result.ConnectorTxnId.ShouldBe("ST-1");
    }

    [Fact]
    public async Task Successful_second_attempt_on_hosted_page_is_accepted()
    {
        // Hosted sayfada reddedilen karttan sonra başka kartla yeniden denenebilir;
        // listenin İLK elemanına bakmak tahsil edilmiş parayı "başarısız" gösterirdi.
        _server.Respond("/api/GetHostedPaymentStatus", HttpStatusCode.OK, """
            {"result":"Success","paymentResult":"Success","paymentList":[
              {"orderId":"att_0001","systemTransId":"ST-1","isSuccess":false,"amountRequested":149.90,
               "resultCategory":{"categoryCode":"LD01"}},
              {"orderId":"att_0001","systemTransId":"ST-1","isSuccess":true,"isCancelled":false,
               "amountRequested":149.90,"acquirerResultDetail":{"pos":{"authCode":"B2"}}}
            ]}
            """);

        var result = await _connector.CompleteHostedCallbackAsync(HostedState(), _credentials, default);

        result.Success.ShouldBeTrue();
        result.AuthCode.ShouldBe("B2");
    }

    [Fact]
    public async Task Auto_cancelled_payment_is_not_success()
    {
        _server.Respond("/api/GetHostedPaymentStatus", HttpStatusCode.OK, """
            {"result":"Success","paymentResult":"Success","paymentList":[
              {"orderId":"att_0001","isSuccess":true,"isCancelled":true,"amountRequested":149.90}]}
            """);

        (await _connector.CompleteHostedCallbackAsync(HostedState(), _credentials, default))
            .Success.ShouldBeFalse();
    }

    [Fact]
    public async Task Ambiguous_result_is_not_success()
    {
        // UnexpectedState: banka onaylamış da olabilir, reddetmiş de. "Ödendi" demek
        // müşteriye mal gönderip parayı almamak olabilirdi; mutabakat netleştirir.
        _server.Respond("/api/GetHostedPaymentStatus", HttpStatusCode.OK,
            """{"result":"Success","paymentResult":"UnexpectedState","paymentList":[]}""");

        var result = await _connector.CompleteHostedCallbackAsync(HostedState(), _credentials, default);

        result.Success.ShouldBeFalse();
        result.UnifiedCode.ShouldBe(UnifiedErrors.ProcessingError);
        result.RawCode.ShouldBe("UnexpectedState");
    }

    [Fact]
    public async Task Amount_mismatch_is_not_success()
    {
        _server.Respond("/api/GetHostedPaymentStatus", HttpStatusCode.OK, SucceededHostedStatus("att_0001", 1.00m));

        var result = await _connector.CompleteHostedCallbackAsync(HostedState(), _credentials, default);

        result.Success.ShouldBeFalse();
        result.RawMessage!.ShouldContain("eşleşmiyor");
    }

    [Fact]
    public async Task Decline_maps_by_category_and_carries_bank_raw_code()
    {
        _server.Respond("/api/GetHostedPaymentStatus", HttpStatusCode.OK, """
            {"result":"Success","paymentResult":"Refused","paymentList":[
              {"orderId":"att_0001","isSuccess":false,"amountRequested":149.90,
               "resultCategory":{"categoryCode":"LD01"},
               "acquirerResultDetail":{"pos":{"returnCode":"51","message":"Limit yetersiz"}}}]}
            """);

        var result = await _connector.CompleteHostedCallbackAsync(HostedState(), _credentials, default);

        result.Success.ShouldBeFalse();
        result.UnifiedCode.ShouldBe(UnifiedErrors.InsufficientFunds);
        result.RawCode.ShouldBe("51");
        result.RawMessage.ShouldBe("Limit yetersiz");
    }

    [Fact]
    public async Task Missing_state_fails_without_calling_server()
    {
        var result = await _connector.CompleteHostedCallbackAsync(new Dictionary<string, string>
        {
            ["OrderId"] = "att_0001",
            ["SystemTransId"] = "ST-1",
            ["Result"] = "Success", // tarayıcının iddiası
        }, _credentials, default);

        result.Success.ShouldBeFalse();
        _server.Paths.ShouldBeEmpty();
    }

    [Fact]
    public void Browser_return_alone_is_never_accepted()
        => _connector.ParseAndValidateCallback(new Dictionary<string, string>
        {
            ["OrderId"] = "att_0001",
            ["Result"] = "Success",
            ["Hash"] = "uydurma",
        }, _credentials).Success.ShouldBeFalse();

    // ---- 3DS'li direct ---------------------------------------------------------

    [Fact]
    public async Task ThreeDs_form_is_extracted_from_html_and_trans_id_kept_in_state()
    {
        _server.Respond("/api/ProcessPayment", HttpStatusCode.OK, """
            {"result":"RedirectFormCreated","resultDetail":"ThreeDSRedirectFromCreated",
             "redirectForm":"<html><body><form method=\"post\" action=\"https://3d.lidio.test/acs\"><input type=\"hidden\" name=\"PaReq\" value=\"XYZ\" /></form><script>document.forms[0].submit()</script></body></html>",
             "paymentInfo":{"orderId":"att_0010","systemTransId":"ST-10"}}
            """);

        var form = await _connector.InitiateThreeDsDirectAsync(
            new DirectPaymentRequest("att_0010", 14_990, "TRY", 2,
                new CardData("4546711234567894", 12, 2030, "POYRA MUSTERI", "123"), "Koltuk", "203.0.113.7"),
            "https://api.poyra.test/v1/callbacks/lidio/tok", _credentials, default);

        form.ShouldNotBeNull();
        form!.ActionUrl.ShouldBe("https://3d.lidio.test/acs");
        form.Fields["PaReq"].ShouldBe("XYZ");
        form.ConnectorState!["poyra_lidio_flow"].ShouldBe("direct");
        form.ConnectorState["poyra_lidio_system_trans_id"].ShouldBe("ST-10");

        var card = JsonDocument.Parse(_server.Last("/api/ProcessPayment").Body).RootElement
            .GetProperty("paymentInstrumentInfo").GetProperty("newCard");
        card.GetProperty("use3DSecure").GetBoolean().ShouldBeTrue();
        card.GetProperty("installmentCount").GetInt32().ShouldBe(2);
        // Kart Lidio'da saklanmaz: Poyra'nın kasası varken ikinci bir kart deposu PCI yükü.
        card.GetProperty("saveAfterSuccess").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ThreeDs_return_settles_via_Finish_and_ignores_browser_trans_id()
    {
        _server.Respond("/api/FinishPaymentProcess", HttpStatusCode.OK, """
            {"result":"Success","resultDetail":"Success","paymentInfo":{
              "orderId":"att_0010","systemTransId":"ST-10","amountRequested":149.90,
              "instrumentDetail":{"card":{"maskedCardNumber":"454671******7894","cardBankName":"Test Bankası"}},
              "acquirerResultDetail":{"pos":{"authCode":"C3"}},
              "resultCategory":{"categoryCode":"LD00"}}}
            """);

        var result = await _connector.CompleteHostedCallbackAsync(new Dictionary<string, string>
        {
            ["poyra_lidio_flow"] = "direct",
            ["poyra_lidio_order_id"] = "att_0010",
            ["poyra_lidio_system_trans_id"] = "ST-10",
            ["poyra_lidio_amount"] = "14990",
            ["poyra_lidio_currency"] = "TRY",
            ["SystemTransId"] = "ST-SALDIRGAN",
            ["Result"] = "3DSuccess",
        }, _credentials, default);

        var body = JsonDocument.Parse(_server.Last("/api/FinishPaymentProcess").Body).RootElement;
        body.GetProperty("systemTransId").GetString().ShouldBe("ST-10");
        body.GetProperty("orderId").GetString().ShouldBe("att_0010");
        body.GetProperty("totalAmount").GetDecimal().ShouldBe(149.90m);

        result.Success.ShouldBeTrue();
        result.AuthCode.ShouldBe("C3");
        result.MaskedPan.ShouldBe("454671******7894");
    }

    [Fact]
    public async Task Failed_ThreeDs_validation_returns_ThreeDs_error()
    {
        _server.Respond("/api/FinishPaymentProcess", HttpStatusCode.OK,
            """{"result":"InvalidParameter","resultDetail":"ThreeDValidationFailed","resultMessage":"3D başarısız"}""");

        var result = await _connector.CompleteHostedCallbackAsync(new Dictionary<string, string>
        {
            ["poyra_lidio_flow"] = "direct",
            ["poyra_lidio_order_id"] = "att_0011",
            ["poyra_lidio_system_trans_id"] = "ST-11",
        }, _credentials, default);

        result.Success.ShouldBeFalse();
        result.UnifiedCode.ShouldBe(UnifiedErrors.ThreeDsFailed);
    }

    // ---- Hata eşlemesi ---------------------------------------------------------

    [Theory]
    [InlineData("LD01", UnifiedErrors.InsufficientFunds)]
    [InlineData("LD02", UnifiedErrors.ExpiredCard)]
    [InlineData("LD04", UnifiedErrors.NotPermitted)]
    [InlineData("LD30", UnifiedErrors.ThreeDsFailed)]
    [InlineData("LD90", UnifiedErrors.IssuerUnavailable)]
    [InlineData("LD99", UnifiedErrors.CardDeclined)]
    public void Category_maps_to_unified_code(string category, string expected)
        => LidioMessages.UnifiedError(category, null).ShouldBe(expected);

    [Theory]
    [InlineData("LD10")] // banka fraud reddi — "bir süre sonra tekrar deneyin"
    [InlineData("LD11")] // müşteri bazlı günlük limit
    [InlineData("LD40")]
    public void Retry_later_category_is_not_mapped_to_permanent_decline(string category)
    {
        // Poyra'da limit_exceeded ve transaction_not_permitted yeniden tahsilatı
        // KALICI olarak bırakır. Lidio'nun "sonra tekrar deneyin" dediği reddi oraya
        // eşlemek kurtarılabilir aboneliği sessizce kaybettirirdi.
        var code = LidioMessages.UnifiedError(category, null);

        code.ShouldNotBe(UnifiedErrors.LimitExceeded);
        code.ShouldNotBe(UnifiedErrors.NotPermitted);
    }

    [Fact]
    public void Unknown_code_is_not_blamed_on_card()
        => LidioMessages.UnifiedError(null, "YeniBirKod").ShouldBe(UnifiedErrors.ProcessingError);

    // ---- İptal / iade ----------------------------------------------------------

    [Fact]
    public async Task Void_uses_order_id()
    {
        _server.Respond("/api/Cancel", HttpStatusCode.OK, """{"result":"Success"}""");

        var result = await _connector.VoidAsync(new ConnectorReference("att_0001", "ST-1"), _credentials, default);

        result.Success.ShouldBeTrue();
        JsonDocument.Parse(_server.Last("/api/Cancel").Body)
            .RootElement.GetProperty("orderId").GetString().ShouldBe("att_0001");
    }

    [Fact]
    public async Task Partial_refund_amount_is_sent_as_decimal()
    {
        _server.Respond("/api/Refund", HttpStatusCode.OK, """{"result":"Success"}""");

        var result = await _connector.RefundAsync(
            new ConnectorRefundRequest("att_0001", "ST-1", 4990, "TRY"), _credentials, default);

        result.Success.ShouldBeTrue();
        JsonDocument.Parse(_server.Last("/api/Refund").Body)
            .RootElement.GetProperty("totalAmount").GetDecimal().ShouldBe(49.90m);
    }

    [Fact]
    public async Task Duplicate_refund_response_counts_as_success()
    {
        _server.Respond("/api/Refund", HttpStatusCode.OK, """{"result":"DuplicateRequest"}""");

        (await _connector.RefundAsync(
                new ConnectorRefundRequest("att_0001", "ST-1", 4990, "TRY"), _credentials, default))
            .Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Pending_refund_is_not_success()
    {
        // Pending "henüz sonuçlanmadı" demek; başarı sayılsaydı para geri gitmeden
        // iade kapanmış görünür ve kimse peşine düşmezdi.
        _server.Respond("/api/Refund", HttpStatusCode.OK, """{"result":"Pending","resultDetail":"Pending"}""");

        (await _connector.RefundAsync(
                new ConnectorRefundRequest("att_0001", "ST-1", 4990, "TRY"), _credentials, default))
            .Success.ShouldBeFalse();
    }

    // ---- Sağlık yoklaması / erişilemezlik --------------------------------------

    [Fact]
    public async Task Probe_treats_invalid_credential_as_unhealthy()
    {
        _server.Respond("/api/PaymentInquiry", HttpStatusCode.OK, """{"result":"InvalidCredential"}""");

        (await _connector.ProbeAsync(_credentials, default))!.Healthy.ShouldBeFalse();
    }

    [Fact]
    public async Task Probe_treats_not_found_as_healthy()
    {
        _server.Respond("/api/PaymentInquiry", HttpStatusCode.OK,
            """{"result":"InvalidParameter","resultDetail":"RefTransactionNotFound"}""");

        (await _connector.ProbeAsync(_credentials, default))!.Healthy.ShouldBeTrue();
    }

    [Fact]
    public async Task Server_5xx_raises_failover_eligible_error()
    {
        _server.Respond("/api/StartHostedPaymentProcess", HttpStatusCode.BadGateway, "{}");

        await Should.ThrowAsync<ConnectorUnavailableException>(
            () => _connector.InitiateHostedPaymentAsync(
                new HostedPaymentRequest("att_0006", 100, "TRY", 1, "https://cb.test", null, null),
                _credentials, default));
    }

    // ---- Sipariş numarası ------------------------------------------------------

    private const string LongAttemptId = "att_0199b3c2d4e57a8b9c0d1e2f3a4b5c6d";

    [Theory]
    [InlineData("att_0001", "att_0001")]                          // kısa ve geçerli: olduğu gibi
    [InlineData(LongAttemptId, "7a8b9c0d1e2f3a4b5c6d")]           // uzun: öneksiz SON 20 karakter
    [InlineData("ref_0199b3c2d4e57a8b9c0d1e2f3a4b5c6e", "7a8b9c0d1e2f3a4b5c6e")]
    [InlineData("poyra-t-1", "poyrat1")]                         // izin verilmeyen karakter atılır
    public void Order_id_fits_Lidio_validation(string poyraId, string expected)
    {
        var orderId = LidioMessages.OrderId(poyraId);

        orderId.ShouldBe(expected);
        orderId.Length.ShouldBeLessThanOrEqualTo(20);
        orderId.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    [Fact]
    public async Task Long_attempt_id_is_shortened_for_Lidio_but_returned_to_Poyra()
    {
        // Sandbox: doğrulaması açık hesapta 20 karakteri aşan sipariş no InvalidOrderId alır.
        // Callback işleyicisi ise sonucu DENEME KİMLİĞİYLE eşleştirir — ikisi karışmamalı.
        _server.Respond("/api/StartHostedPaymentProcess", HttpStatusCode.OK,
            """{"result":"Success","systemTransId":"ST-7","redirectURL":"https://pay.lidio.test/h/7"}""");

        var form = await _connector.InitiateHostedPaymentAsync(
            new HostedPaymentRequest(LongAttemptId, 14_990, "TRY", 1, "https://cb.test", null, null),
            _credentials, default);

        var body = JsonDocument.Parse(_server.Last("/api/StartHostedPaymentProcess").Body).RootElement;
        body.GetProperty("orderId").GetString().ShouldBe("7a8b9c0d1e2f3a4b5c6d");
        form.ConnectorState!["poyra_lidio_order_id"].ShouldBe("7a8b9c0d1e2f3a4b5c6d");
        form.ConnectorState["poyra_lidio_attempt_id"].ShouldBe(LongAttemptId);

        _server.Respond("/api/GetHostedPaymentStatus", HttpStatusCode.OK,
            SucceededHostedStatus("7a8b9c0d1e2f3a4b5c6d", 149.90m));

        var result = await _connector.CompleteHostedCallbackAsync(
            new Dictionary<string, string>(form.ConnectorState), _credentials, default);

        result.Success.ShouldBeTrue();
        result.OrderId.ShouldBe(LongAttemptId);
        JsonDocument.Parse(_server.Last("/api/GetHostedPaymentStatus").Body)
            .RootElement.GetProperty("orderId").GetString().ShouldBe("7a8b9c0d1e2f3a4b5c6d");
    }

    [Fact]
    public async Task Void_and_refund_regenerate_the_same_Lidio_order_id()
    {
        _server.Respond("/api/Cancel", HttpStatusCode.OK, """{"result":"Success"}""");
        _server.Respond("/api/Refund", HttpStatusCode.OK, """{"result":"Success"}""");

        await _connector.VoidAsync(new ConnectorReference(LongAttemptId, "ST-1"), _credentials, default);
        await _connector.RefundAsync(new ConnectorRefundRequest(LongAttemptId, "ST-1", 100, "TRY"),
            _credentials, default);

        JsonDocument.Parse(_server.Last("/api/Cancel").Body)
            .RootElement.GetProperty("orderId").GetString().ShouldBe("7a8b9c0d1e2f3a4b5c6d");
        JsonDocument.Parse(_server.Last("/api/Refund").Body)
            .RootElement.GetProperty("orderId").GetString().ShouldBe("7a8b9c0d1e2f3a4b5c6d");
    }

    [Fact]
    public async Task State_without_attempt_id_from_older_attempts_still_completes()
    {
        // Deneme kimliği alanı eklenmeden önce başlatılmış (henüz dönmemiş) denemeler.
        _server.Respond("/api/GetHostedPaymentStatus", HttpStatusCode.OK, SucceededHostedStatus("att_0001", 149.90m));

        var result = await _connector.CompleteHostedCallbackAsync(HostedState(), _credentials, default);

        result.Success.ShouldBeTrue();
        result.OrderId.ShouldBe("att_0001");
    }

    // ---- Taksit / ödeme gövdesi ------------------------------------------------

    [Theory]
    [InlineData(1, 0)] // belge: tek çekimde 0 gönderilir, 1 DEĞİL
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    public void Installment_count_uses_zero_for_single_payment(int installments, int expected)
        => LidioMessages.InstallmentCount(installments).ShouldBe(expected);

    [Fact]
    public async Task Single_payment_sends_zero_installments_and_no_loyalty_usage()
    {
        _server.Respond("/api/ProcessPayment", HttpStatusCode.OK, """
            {"result":"RedirectFormCreated",
             "redirectForm":"<form method=\"post\" action=\"https://3d.lidio.test/acs\"></form>",
             "paymentInfo":{"systemTransId":"ST-12"}}
            """);

        await _connector.InitiateThreeDsDirectAsync(
            new DirectPaymentRequest("att_0012", 1_000, "TRY", 1,
                new CardData("4546711234567894", 12, 2030, null, "123"), null, null),
            "https://cb.test", _credentials, default);

        var card = JsonDocument.Parse(_server.Last("/api/ProcessPayment").Body).RootElement
            .GetProperty("paymentInstrumentInfo").GetProperty("newCard");
        card.GetProperty("installmentCount").GetInt32().ShouldBe(0);
        // Belgede zorunlu alan; puan kullanımı müşteriye sorulmadan açılmamalı.
        card.GetProperty("loyaltyPointUsage").GetString().ShouldBe("None");
        card.GetProperty("cardInfo").GetProperty("cardHolderName").GetString().ShouldBe("POYRA MUSTERI");
    }

    // ---- 3D'siz direct ---------------------------------------------------------

    [Fact]
    public async Task Direct_sale_without_3ds_settles_in_one_call()
    {
        _server.Respond("/api/ProcessPayment", HttpStatusCode.OK, """
            {"result":"Success","resultDetail":"Success","paymentInfo":{
              "orderId":"att_0020","systemTransId":200963426,"amountRequested":10.50,
              "instrumentDetail":{"card":{"maskedCardNumber":"554960******0013"}},
              "acquirerResultDetail":{"pos":{"authCode":"254529","returnCode":"00"}},
              "resultCategory":{"categoryCode":"LD00"}}}
            """);

        var result = await _connector.AuthorizeDirectAsync(
            new DirectPaymentRequest("att_0020", 1_050, "TRY", 1,
                new CardData("5549602257210013", 2, 2030, "POYRA TEST", "689"), null, "203.0.113.7"),
            _credentials, default);

        result.ShouldNotBeNull();
        result!.Success.ShouldBeTrue();
        result.AuthCode.ShouldBe("254529");
        result.ConnectorTxnId.ShouldBe("200963426"); // sayı olarak dönse de metin olarak okunur
        result.MaskedPan.ShouldBe("554960******0013");

        var body = JsonDocument.Parse(_server.Last("/api/ProcessPayment").Body).RootElement;
        body.GetProperty("paymentInstrumentInfo").GetProperty("newCard")
            .GetProperty("use3DSecure").GetBoolean().ShouldBeFalse();
        body.TryGetProperty("returnUrl", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Direct_sale_decline_maps_bank_category()
    {
        _server.Respond("/api/ProcessPayment", HttpStatusCode.OK, """
            {"result":"Refused","resultDetail":"Refused","paymentInfo":{
              "resultCategory":{"categoryCode":"LD01"},
              "acquirerResultDetail":{"pos":{"returnCode":"51","message":"Limit yetersiz"}}}}
            """);

        var result = await _connector.AuthorizeDirectAsync(
            new DirectPaymentRequest("att_0021", 1_050, "TRY", 1,
                new CardData("5549602257210013", 2, 2030, null, "689"), null, null),
            _credentials, default);

        result!.Success.ShouldBeFalse();
        result.UnifiedCode.ShouldBe(UnifiedErrors.InsufficientFunds);
        result.RawCode.ShouldBe("51");
    }

    [Fact]
    public async Task Direct_sale_with_wrong_amount_is_not_success()
    {
        _server.Respond("/api/ProcessPayment", HttpStatusCode.OK, """
            {"result":"Success","paymentInfo":{"orderId":"att_0022","amountRequested":1.00}}
            """);

        var result = await _connector.AuthorizeDirectAsync(
            new DirectPaymentRequest("att_0022", 1_050, "TRY", 1,
                new CardData("5549602257210013", 2, 2030, null, "689"), null, null),
            _credentials, default);

        result!.Success.ShouldBeFalse();
        result.RawMessage!.ShouldContain("eşleşmiyor");
    }

    // ---- İade kimliği ----------------------------------------------------------

    [Fact]
    public async Task Refund_sends_refund_id_as_idempotency_key_and_returns_refund_txn()
    {
        _server.Respond("/api/Refund", HttpStatusCode.OK,
            """{"result":"Success","paymentInfo":{"systemTransId":"200963440"}}""");

        var result = await _connector.RefundAsync(
            new ConnectorRefundRequest("att_0001", "ST-1", 500, "TRY",
                RefundId: "ref_0199b3c2d4e57a8b9c0d1e2f3a4b5c6e"),
            _credentials, default);

        result.Success.ShouldBeTrue();
        result.ConnectorTxnId.ShouldBe("200963440"); // asıl satışın değil iadenin numarası
        JsonDocument.Parse(_server.Last("/api/Refund").Body)
            .RootElement.GetProperty("refundTransId").GetString().ShouldBe("7a8b9c0d1e2f3a4b5c6e");
    }

    [Fact]
    public async Task Refund_without_refund_id_sends_no_key()
    {
        // Kimliksiz iki aynı tutarlı kısmi iade Lidio'da AYRI iadelerdir (sandbox'ta doğrulandı);
        // uydurma bir anahtar ikinciyi yanlışlıkla "mükerrer" yapardı.
        _server.Respond("/api/Refund", HttpStatusCode.OK, """{"result":"Success"}""");

        await _connector.RefundAsync(new ConnectorRefundRequest("att_0001", "ST-1", 500, "TRY"),
            _credentials, default);

        JsonDocument.Parse(_server.Last("/api/Refund").Body)
            .RootElement.TryGetProperty("refundTransId", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Over_refund_is_reported_with_lidio_code()
    {
        _server.Respond("/api/Refund", HttpStatusCode.OK, """{"result":"Refused","resultDetail":"Refused"}""");

        var result = await _connector.RefundAsync(new ConnectorRefundRequest("att_0001", "ST-1", 99_999, "TRY"),
            _credentials, default);

        result.Success.ShouldBeFalse();
        result.RawCode.ShouldBe("Refused");
        result.UnifiedCode.ShouldBe(UnifiedErrors.CardDeclined);
    }

    // ---- Yetki / hash ----------------------------------------------------------

    [Fact]
    public async Task Unauthorized_http_status_is_a_configuration_error()
    {
        // Yetkisiz metot 401 problem+json döner — sonuç kodu taşımaz.
        _server.Respond("/api/PaymentInquiry", HttpStatusCode.Unauthorized,
            """{"title":"Unauthorized","status":401}""");

        var probe = await _connector.ProbeAsync(_credentials, default);

        probe!.Healthy.ShouldBeFalse();
        probe.Detail!.ShouldContain("401");
    }

    [Fact]
    public void Api_key_splits_into_merchant_key_and_password()
    {
        var apiKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("merchant-key-1:parola:iki-nokta"));

        var (merchantKey, apiPassword) = LidioMessages.SplitApiKey(apiKey);

        merchantKey.ShouldBe("merchant-key-1");
        apiPassword.ShouldBe("parola:iki-nokta"); // yalnız İLK ':' ayırıcıdır
    }

    [Fact]
    public void Malformed_api_key_is_a_configuration_error()
        => Should.Throw<ConnectorConfigurationException>(() => LidioMessages.SplitApiKey("base64-degil!"));

    [Fact]
    public void Notification_hash_is_checked_against_raw_body()
    {
        const string body = """{"paymentResult":"Success","processInfo":{"orderId":"att_0001"}}""";
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(body + "parola")));

        LidioMessages.VerifyNotification(body, hash, "parola").ShouldBeTrue();
        LidioMessages.VerifyNotification(body.Replace("Success", "Failed"), hash, "parola").ShouldBeFalse();
        LidioMessages.VerifyNotification(body, hash, "baska-parola").ShouldBeFalse();
        LidioMessages.VerifyNotification(body, null, "parola").ShouldBeFalse();
    }

    [Fact]
    public void Return_hash_formats_amount_in_en_us_with_two_decimals()
    {
        // Türkçe kültürde 149,9 yazılırsa hash hiç tutmaz.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes("att_0001:mk:149.90:3DSuccess:att_0001")));

            LidioMessages.ReturnHash("att_0001", "mk", 149.9m, "3DSuccess", "att_0001").ShouldBe(expected);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ---- Yardımcılar -----------------------------------------------------------

    private static Dictionary<string, string> HostedState() => new()
    {
        ["poyra_lidio_flow"] = "hosted",
        ["poyra_lidio_order_id"] = "att_0001",
        ["poyra_lidio_system_trans_id"] = "ST-1",
        ["poyra_lidio_amount"] = "14990",
    };

    private static string SucceededHostedStatus(string orderId, decimal amount) => $$$"""
        {"result":"Success","paymentResult":"Success","paymentList":[
          {"orderId":"{{{orderId}}}","systemTransId":"ST-1","isSuccess":true,"isCancelled":false,
           "amountRequested":{{{amount.ToString(CultureInfo.InvariantCulture)}}},
           "instrumentDetail":{"card":{"maskedCardNumber":"454671******7894","cardBankName":"Test Bankası"}},
           "acquirerResultDetail":{"pos":{"authCode":"A12345","returnCode":"00"}},
           "resultCategory":{"categoryCode":"LD00"}}]}
        """;
}
