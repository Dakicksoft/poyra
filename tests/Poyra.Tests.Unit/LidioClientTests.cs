using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Lidio;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Lidio istemcisinin birleşik arayüz dışındaki metotları (saklı kart, ön provizyon,
/// hosted ön ödeme, taksit sorgusu) — sahte sunucuya giden GÖVDE belgedeki şemayla
/// karşılaştırılır. Sağlayıcının gerçekten kabul ettiğini sandbox testleri kanıtlar
/// (<c>tests/Poyra.Tests.Sandbox</c>); burası o gövdenin sessizce bozulmasını yakalar.
/// </summary>
public sealed class LidioClientTests : IAsyncLifetime
{
    private static readonly CardData Card = new("5549602257210013", 2, 2030, "POYRA TEST", "689");
    private static readonly LidioCustomer Customer = new("cus_1", "musteri@poyra.test");

    private FakeLidio _server = null!;
    private LidioClient _client = null!;
    private ConnectorCredentials _credentials = null!;

    public async Task InitializeAsync()
    {
        _server = new FakeLidio();
        await _server.StartAsync();

        var services = new ServiceCollection();
        services.AddHttpClient(LidioConnector.HttpClientName);
        _client = new LidioClient(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());
        _credentials = new ConnectorCredentials(new Dictionary<string, string>
        {
            ["gateway_base"] = _server.BaseUrl + "/api",
            ["merchant_code"] = "POYRA_TEST",
            ["api_key"] = "SIR-lidio-api-9F3A",
        });
    }

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    // ---- Ödeme ---------------------------------------------------------------------

    [Fact]
    public async Task Preauth_uses_preauth_process_type()
    {
        _server.Respond("/api/ProcessPayment", HttpStatusCode.OK, """{"result":"Success"}""");

        await _client.ProcessPaymentAsync(new LidioCardPayment
        {
            OrderId = "pa_1", AmountMinor = 5_000, Currency = "TRY", Customer = Customer,
            ProcessType = LidioProcessTypes.PreAuth, NewCard = Card,
        }, _credentials, default);

        var body = Body("/api/ProcessPayment");
        body.GetProperty("paymentInstrument").GetString().ShouldBe("NewCard");
        body.GetProperty("totalAmount").GetDecimal().ShouldBe(50.00m);
        body.GetProperty("customerInfo").GetProperty("email").GetString().ShouldBe("musteri@poyra.test");
        body.GetProperty("paymentInstrumentInfo").GetProperty("newCard")
            .GetProperty("processType").GetString().ShouldBe("preauth");
    }

    [Fact]
    public async Task Stored_card_payment_sends_token_and_verification_methods()
    {
        _server.Respond("/api/ProcessPayment", HttpStatusCode.OK,
            """{"result":"VerificationRequired","resultDetail":"CVVRequired","paymentInfo":{"systemTransId":"200963443"}}""");

        var response = await _client.ProcessPaymentAsync(new LidioCardPayment
        {
            OrderId = "ps_1", AmountMinor = 725, Currency = "TRY", Customer = Customer,
            StoredCardToken = "tok-1", VerificationMethods = ["CVV"],
        }, _credentials, default);

        response.RequiresVerification.ShouldBeTrue();
        response.PaymentInfo!.SystemTransId.ShouldBe("200963443");

        var body = Body("/api/ProcessPayment");
        body.GetProperty("paymentInstrument").GetString().ShouldBe("StoredCard");
        var stored = body.GetProperty("paymentInstrumentInfo").GetProperty("storedCard");
        stored.GetProperty("cardToken").GetString().ShouldBe("tok-1");
        stored.GetProperty("verificationInfo").GetProperty("verificationMethods")
            .EnumerateArray().Select(m => m.GetString()).ShouldBe(["CVV"]);
        stored.GetProperty("loyaltyPointUsage").GetString().ShouldBe("None");
        body.GetProperty("paymentInstrumentInfo").TryGetProperty("newCard", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Payment_requires_exactly_one_card_source()
    {
        await Should.ThrowAsync<ArgumentException>(() => _client.ProcessPaymentAsync(new LidioCardPayment
        {
            OrderId = "x", AmountMinor = 1, Currency = "TRY", Customer = Customer,
        }, _credentials, default));

        await Should.ThrowAsync<ArgumentException>(() => _client.ProcessPaymentAsync(new LidioCardPayment
        {
            OrderId = "x", AmountMinor = 1, Currency = "TRY", Customer = Customer,
            NewCard = Card, StoredCardToken = "tok",
        }, _credentials, default));

        _server.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task Finish_for_stored_card_carries_cvv_under_stored_card()
    {
        _server.Respond("/api/FinishPaymentProcess", HttpStatusCode.OK, """{"result":"Success"}""");

        await _client.FinishPaymentAsync(new LidioFinishPayment
        {
            OrderId = "ps_1", SystemTransId = "200963443", AmountMinor = 725, Currency = "TRY",
            StoredCard = true, Cvv = "689",
        }, _credentials, default);

        var body = Body("/api/FinishPaymentProcess");
        body.GetProperty("paymentInstrument").GetString().ShouldBe("StoredCard");
        body.GetProperty("paymentInstrumentInfo").GetProperty("storedCard")
            .GetProperty("cvv").GetString().ShouldBe("689");
        body.GetProperty("totalAmount").GetDecimal().ShouldBe(7.25m);
    }

    [Fact]
    public async Task Postauth_sends_closing_amount()
    {
        _server.Respond("/api/Postauth", HttpStatusCode.OK, """{"result":"Success"}""");

        await _client.PostauthAsync("pa_1", 4_000, "TRY", _credentials, default);

        var body = Body("/api/Postauth");
        body.GetProperty("orderId").GetString().ShouldBe("pa_1");
        body.GetProperty("totalAmount").GetDecimal().ShouldBe(40.00m);
    }

    [Fact]
    public async Task Inquiry_reads_payment_and_refund_total()
    {
        _server.Respond("/api/PaymentInquiry", HttpStatusCode.OK, """
            {"result":"Success","totalRefund":15.0,"paymentInfo":{"orderId":"pt_1","systemTransId":"200963440",
             "amountProcessed":5.00000,"instrumentDetail":{"card":{"processType":"refund","refTransType":"sales"}}}}
            """);

        var response = await _client.PaymentInquiryAsync("pt_1", _credentials, default, LidioProcessTypes.Refund);

        response.TotalRefund.ShouldBe(15.0m);
        response.PaymentInfo!.Card!.ProcessType.ShouldBe("refund");
        response.PaymentInfo.AmountProcessed.ShouldBe(5.00m);
        Body("/api/PaymentInquiry").GetProperty("paymentInquiryInstrumentInfo").GetProperty("card")
            .GetProperty("processType").GetString().ShouldBe("refund");
    }

    [Fact]
    public async Task Installment_info_parses_pos_options()
    {
        _server.Respond("/api/GetInstallmentInfo", HttpStatusCode.OK, """
            {"result":"Success","posList":[{"posId":1,"posBankName":"LidioPOS","installmentOptionList":[
              {"installmentCount":0,"interestRateToUser":0.0,"totalAmountWithInterest":100.00},
              {"installmentCount":3,"interestRateToUser":4.5,"totalAmountWithInterest":104.50}]}]}
            """);

        var response = await _client.GetInstallmentInfoAsync("554960", 10_000, _credentials, default);

        var options = response.PosList!.Single().InstallmentOptionList!;
        options.Select(o => o.InstallmentCount).ShouldBe([0, 3]);
        options[1].TotalAmountWithInterest.ShouldBe(104.50m);
        Body("/api/GetInstallmentInfo").GetProperty("amount").GetDecimal().ShouldBe(100.00m);
    }

    // ---- Hosted --------------------------------------------------------------------

    [Fact]
    public async Task Hosted_prepayment_uses_its_own_endpoint_and_can_offer_stored_cards()
    {
        _server.Respond("/api/StartHostedPrePaymentProcess", HttpStatusCode.OK,
            """{"result":"Success","systemTransId":"200963449","redirectURL":"https://pay.lidio.test/pp"}""");

        var response = await _client.StartHostedPrePaymentAsync(new LidioHostedPayment
        {
            OrderId = "pp_1", AmountMinor = 999, Currency = "TRY", Customer = Customer,
            ReturnUrl = "https://cb.test", AllowStoredCards = true,
        }, _credentials, default);

        response.RedirectUrl.ShouldBe("https://pay.lidio.test/pp");
        var body = Body("/api/StartHostedPrePaymentProcess");
        body.GetProperty("paymentInstruments").EnumerateArray().Select(i => i.GetString())
            .ShouldBe(["NewCard", "StoredCard"]);
        body.GetProperty("paymentInstrumentInfo").GetProperty("card").GetProperty("storedCard")
            .GetProperty("customerIsLoggedIn").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Hosted_status_parses_attempt_list()
    {
        _server.Respond("/api/GetHostedPaymentStatus", HttpStatusCode.OK, """
            {"result":"Success","paymentResult":"NewProcess","paymentList":[]}
            """);

        var response = await _client.GetHostedPaymentStatusAsync("pt_1", "200963441", _credentials, default);

        response.IsSuccess.ShouldBeTrue();
        response.PaymentResult.ShouldBe("NewProcess"); // müşteri henüz ödemedi
        response.PaymentList.ShouldBeEmpty();
    }

    [Fact]
    public async Task Account_management_page_targets_customer_cards()
    {
        _server.Respond("/api/StartHostedAccountManagement", HttpStatusCode.OK,
            """{"result":"Success","redirectURL":"https://pay.lidio.test/am"}""");

        await _client.StartHostedAccountManagementAsync(Customer, "https://cb.test", threeDSecure: false,
            _credentials, default);

        var body = Body("/api/StartHostedAccountManagement");
        body.GetProperty("customerInfo").GetProperty("customerId").GetString().ShouldBe("cus_1");
        body.GetProperty("accountInstruments").EnumerateArray().Single().GetString().ShouldBe("Card");
        body.GetProperty("accountInstrumentInfo").GetProperty("card")
            .GetProperty("threeDSecureMode").GetString().ShouldBe("None");
    }

    // ---- Saklı kart ----------------------------------------------------------------

    [Fact]
    public async Task Save_card_sends_card_fields_and_returns_token()
    {
        _server.Respond("/api/SaveCard", HttpStatusCode.OK,
            """{"result":"Success","cardToken":"4ea19e27","cardNamebyUser":"Is karti"}""");

        var response = await _client.SaveCardAsync(new LidioSaveCard
        {
            Customer = Customer, Card = Card, CardNameByUser = "Is karti", ClientIp = "203.0.113.7",
        }, _credentials, default);

        response.CardToken.ShouldBe("4ea19e27");
        var body = Body("/api/SaveCard");
        body.GetProperty("cardNumber").GetString().ShouldBe("5549602257210013");
        body.GetProperty("cardMonth").GetInt32().ShouldBe(2);
        body.GetProperty("cardYear").GetInt32().ShouldBe(2030);
        body.GetProperty("clientIp").GetString().ShouldBe("203.0.113.7");
        // CVV saklanmaz — kart kaydı isteğinde hiç bulunmamalı.
        _server.Last("/api/SaveCard").Body.ShouldNotContain("689");
    }

    [Fact]
    public async Task Save_card_from_temporary_token_sends_no_card_number()
    {
        _server.Respond("/api/SaveCard", HttpStatusCode.OK, """{"result":"Success","cardToken":"kalici"}""");

        await _client.SaveCardAsync(new LidioSaveCard
        {
            Customer = Customer, TemporaryCardToken = "gecici-1", ClientIp = "203.0.113.7",
        }, _credentials, default);

        var body = Body("/api/SaveCard");
        body.GetProperty("temporaryCardToken").GetString().ShouldBe("gecici-1");
        body.TryGetProperty("cardNumber", out _).ShouldBeFalse(); // belge: token varken kart no boş
    }

    [Fact]
    public async Task Update_card_always_sends_expiry()
    {
        // Sandbox: son kullanma tarihi gönderilmeyen güncelleme InvalidMonth ile reddedildi.
        _server.Respond("/api/UpdateCardInfo", HttpStatusCode.OK, """{"result":"Success","cardToken":"tok-1"}""");

        await _client.UpdateCardInfoAsync(new LidioUpdateCard
        {
            Customer = Customer, CardToken = "tok-1", ExpiryMonth = 3, ExpiryYear = 2031, CardNameByUser = "Yeni",
        }, _credentials, default);

        var body = Body("/api/UpdateCardInfo");
        body.GetProperty("cardMonth").GetInt32().ShouldBe(3);
        body.GetProperty("cardYear").GetInt32().ShouldBe(2031);
        body.GetProperty("cardNamebyUser").GetString().ShouldBe("Yeni");
    }

    [Fact]
    public async Task Card_list_and_card_info_are_parsed()
    {
        _server.Respond("/api/GetCardList", HttpStatusCode.OK, """
            {"result":"Success","cardList":[{"isDefault":false,"maskedCardNumber":"554960******0013",
             "cardToken":"tok-1","cardNamebyUser":"Is karti","bankCode":"062","cardType":"Mastercard","binNumber":"554960"}]}
            """);
        _server.Respond("/api/TokenToCardInfoInquiry", HttpStatusCode.OK, """
            {"result":"Success","customerID":"cus_1","cardInfo":{"cardToken":"tok-1","isExpired":false}}
            """);

        var list = await _client.GetCardListAsync("cus_1", _credentials, default);
        var info = await _client.TokenToCardInfoInquiryAsync("cus_1", "tok-1", _credentials, default);

        list.CardList!.Single().MaskedCardNumber.ShouldBe("554960******0013");
        list.CardList!.Single().CardType.ShouldBe("Mastercard");
        info.CustomerId.ShouldBe("cus_1");
        info.CardInfo!.IsExpired.ShouldBe(false);
    }

    /// <summary>Her saklı kart metodu belgedeki uca ve zorunlu alanlarıyla gider.</summary>
    [Theory]
    [InlineData("/api/SendOTPForCardSave", "phone")]
    [InlineData("/api/SendOTPForCardRetrieve", "phone")]
    [InlineData("/api/RetrieveCards", "verificationOtp")]
    [InlineData("/api/DeleteCard", "cardToken")]
    [InlineData("/api/DeleteAllCardsOfCustomer", "customerId")]
    [InlineData("/api/CardToTokenInquiry", "cardNumber")]
    [InlineData("/api/CopyStoredCard", "toCustomerId")]
    [InlineData("/api/SendOTPforConsentUpdate", "customerId")]
    [InlineData("/api/UpdateConsent", "clientIp")]
    [InlineData("/api/SendOTPForCardUpdate", "phone")]
    [InlineData("/api/GetBankOfBINNumber", "bin")]
    public async Task Stored_card_method_hits_documented_endpoint(string path, string requiredField)
    {
        _server.Respond(path, HttpStatusCode.OK, """{"result":"InvalidCredential"}""");

        LidioResponse response = path switch
        {
            "/api/SendOTPForCardSave" => await _client.SendOtpForCardSaveAsync("5551234567", _credentials, default),
            "/api/SendOTPForCardRetrieve" => await _client.SendOtpForCardRetrieveAsync("cus_1", "5551234567", _credentials, default),
            "/api/RetrieveCards" => await _client.RetrieveCardsAsync("cus_1", "5551234567", "123456", _credentials, default),
            "/api/DeleteCard" => await _client.DeleteCardAsync("cus_1", "tok-1", _credentials, default),
            "/api/DeleteAllCardsOfCustomer" => await _client.DeleteAllCardsOfCustomerAsync("cus_1", _credentials, default),
            "/api/CardToTokenInquiry" => await _client.CardToTokenInquiryAsync("5549602257210013", _credentials, default),
            "/api/CopyStoredCard" => await _client.CopyStoredCardAsync("cus_1", "cus_2", "tok-1", _credentials, default),
            "/api/SendOTPforConsentUpdate" => await _client.SendOtpForConsentUpdateAsync("cus_1", "5551234567", _credentials, default),
            "/api/UpdateConsent" => await _client.UpdateConsentAsync("cus_1", "5551234567", "123456", "203.0.113.7", _credentials, default),
            "/api/SendOTPForCardUpdate" => await _client.SendOtpForCardUpdateAsync("5551234567", _credentials, default),
            "/api/GetBankOfBINNumber" => await _client.GetBankOfBinNumberAsync("554960", _credentials, default),
            _ => throw new InvalidOperationException(path),
        };

        // Lidio metot yetkisi olmayan hesaba kimlik hatasıyla aynı kodu döner.
        response.IsNotAuthorized.ShouldBeTrue();
        Body(path).TryGetProperty(requiredField, out var value).ShouldBeTrue();
        value.ValueKind.ShouldNotBe(JsonValueKind.Null);
        _server.Last(path).Headers["MerchantCode"].ShouldBe("POYRA_TEST");
    }

    [Fact]
    public async Task Missing_credentials_fail_before_any_request()
    {
        var empty = new ConnectorCredentials(new Dictionary<string, string>());

        await Should.ThrowAsync<ConnectorConfigurationException>(
            () => _client.GetCardListAsync("cus_1", empty, default));
        _server.Paths.ShouldBeEmpty();
    }

    private JsonElement Body(string path) => JsonDocument.Parse(_server.Last(path).Body).RootElement;
}
