using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Lidio;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Poyra.Tests.Sandbox;

/// <summary>
/// 3D Secure ve hosted sayfa akışları — Lidio test ortamı + Garanti 3D simülatörü, gerçek
/// tarayıcıyla. Her test Poyra'nın gerçek sırasını izler: konnektör başlatır, müşteri
/// tarayıcısı bankadan döner, dönüş konnektör durumuyla birleştirilir (callback
/// işleyicisindeki gibi: tarayıcının alanı kazanır) ve sonuç SUNUCU çağrısıyla kesinleşir.
/// </summary>
[Collection(LidioSandboxCollection.Name)]
public sealed class LidioThreeDsSandboxTests(SandboxBrowser browser, ITestOutputHelper output)
{
    private readonly LidioConnector _connector = LidioSandbox.Connector();
    private readonly LidioClient _client = LidioSandbox.Client();
    private readonly ConnectorCredentials _credentials = LidioSandbox.Credentials;

    [LidioSandboxFact]
    public async Task ThreeDs_direct_sale_settles_after_bank_verification_and_refunds()
    {
        var attemptId = LidioSandbox.NewAttemptId();
        var orderId = LidioMessages.OrderId(attemptId);
        var form = await StartThreeDsAsync(attemptId, 1_00);

        var returned = await browser.CompleteThreeDsAsync(form, LidioTestCards.GarantiOtp, output.WriteLine);
        output.WriteLine($"3D dönüşü: {returned.Url}");

        // Lidio dönüşü sorgu dizesiyle bildirir — parayı kanıtlamaz, ama biçimi sabittir.
        returned.Fields["Result"].ShouldBe("3DSuccess");
        returned.Fields["OrderId"].ShouldBe(orderId);
        returned.Fields["TotalAmount"].ShouldBe("1.00");
        // Dönüş hash'i belgedeki formülle (müşteri tekil alanı = müşteri no) birebir tutmalı.
        returned.Fields["Hash"].ShouldBe(
            LidioMessages.ReturnHash(orderId, LidioSandbox.MerchantKey, 1.00m, "3DSuccess", orderId));

        var result = await _connector.CompleteHostedCallbackAsync(
            PoyraCallback.Merge(form.ConnectorState, returned.Fields), _credentials, default);
        output.WriteLine($"Sonuç: {result.Success} {result.UnifiedCode} {result.RawCode} {result.RawMessage}");

        result.Success.ShouldBeTrue(result.RawMessage);
        result.OrderId.ShouldBe(attemptId); // callback işleyicisi deneme kimliğiyle eşleştirir
        result.AuthCode.ShouldNotBeNullOrEmpty();
        result.MaskedPan.ShouldBe("554960******0013");
        result.CardBank.ShouldBe("Garanti");
        result.ConnectorTxnId.ShouldNotBeNullOrEmpty();

        // Para gerçekten çekildi mi — sunucu kayıtları:
        var inquiry = await _client.PaymentInquiryAsync(orderId, _credentials, default);
        inquiry.IsSuccess.ShouldBeTrue();
        inquiry.PaymentInfo!.AmountProcessed.ShouldBe(1.00m);
        inquiry.PaymentInfo.Card!.Is3DSecure.ShouldBe(true);

        // 3D ile çekilen para iade edilebilmeli.
        var refund = await _connector.RefundAsync(
            new ConnectorRefundRequest(attemptId, result.ConnectorTxnId, 1_00, "TRY"), _credentials, default);
        refund.Success.ShouldBeTrue(refund.RawMessage);
    }

    [LidioSandboxFact]
    public async Task ThreeDs_cancelled_on_bank_page_is_declined_and_takes_no_money()
    {
        var attemptId = LidioSandbox.NewAttemptId();
        var form = await StartThreeDsAsync(attemptId, 5_00);

        var returned = await browser.CompleteThreeDsAsync(form, otp: null, output.WriteLine);
        output.WriteLine($"3D dönüşü: {returned.Url}");
        returned.Fields["Result"].ShouldBe("3DFailed");

        // Tarayıcı "başarılı" deseydi bile karar Finish çağrısınındır; burada iki taraf da red.
        var result = await _connector.CompleteHostedCallbackAsync(
            PoyraCallback.Merge(form.ConnectorState, returned.Fields), _credentials, default);

        result.Success.ShouldBeFalse();
        result.OrderId.ShouldBe(attemptId);
        result.UnifiedCode.ShouldBe(UnifiedErrors.ThreeDsFailed);
        output.WriteLine($"Ret: {result.RawCode} / {result.RawMessage}");

        var inquiry = await _client.PaymentInquiryAsync(LidioMessages.OrderId(attemptId), _credentials, default);
        inquiry.IsSuccess.ShouldBeFalse();
    }

    [LidioSandboxFact]
    public async Task Tampered_browser_return_cannot_turn_a_failed_payment_into_success()
    {
        var attemptId = LidioSandbox.NewAttemptId();
        var form = await StartThreeDsAsync(attemptId, 4_00);
        var returned = await browser.CompleteThreeDsAsync(form, otp: null, output.WriteLine);

        // Saldırgan dönüş adresini "3DSuccess" ile kendisi çağırır.
        var forged = new Dictionary<string, string>(returned.Fields) { ["Result"] = "3DSuccess", ["MDStatus"] = "1" };

        var result = await _connector.CompleteHostedCallbackAsync(
            PoyraCallback.Merge(form.ConnectorState, forged), _credentials, default);

        result.Success.ShouldBeFalse();
        result.UnifiedCode.ShouldBe(UnifiedErrors.ThreeDsFailed);
    }

    [LidioSandboxFact]
    public async Task Hosted_page_sale_is_confirmed_by_server_query_and_voids()
    {
        var attemptId = LidioSandbox.NewAttemptId();
        var form = await _connector.InitiateHostedPaymentAsync(
            new HostedPaymentRequest(attemptId, 15_75, "TRY", 1, SandboxBrowser.NewReturnUrl(), "Sandbox hosted", "127.0.0.1"),
            _credentials, default);

        form.Method.ShouldBe("GET");
        form.ActionUrl.ShouldStartWith("https://test.lidio.com/");

        var returned = await browser.CompleteHostedPageAsync(form.ActionUrl, LidioTestCards.Garanti,
            LidioTestCards.GarantiOtp, output.WriteLine);
        output.WriteLine($"Hosted dönüşü: {returned.Url}");

        var result = await _connector.CompleteHostedCallbackAsync(
            PoyraCallback.Merge(form.ConnectorState, returned.Fields), _credentials, default);
        output.WriteLine($"Sonuç: {result.Success} {result.UnifiedCode} {result.RawCode} {result.RawMessage}");

        result.Success.ShouldBeTrue(result.RawMessage);
        result.OrderId.ShouldBe(attemptId);
        result.MaskedPan.ShouldBe("554960******0013");
        result.AuthCode.ShouldNotBeNullOrEmpty();

        // Gün içinde iptal: komisyon doğmadan tamamı geri alınır.
        var voided = await _connector.VoidAsync(new ConnectorReference(attemptId, result.ConnectorTxnId),
            _credentials, default);
        voided.Success.ShouldBeTrue(voided.RawMessage);

        var status = await _client.GetHostedPaymentStatusAsync(
            LidioMessages.OrderId(attemptId), form.ConnectorState!["poyra_lidio_system_trans_id"], _credentials, default);
        output.WriteLine($"İptal sonrası hosted durum: {status.PaymentResult}");
    }

    [LidioSandboxFact]
    public async Task Hosted_prepayment_takes_no_money_until_finish()
    {
        var orderId = LidioSandbox.NewOrderId("pp");
        var start = await _client.StartHostedPrePaymentAsync(new LidioHostedPayment
        {
            OrderId = orderId,
            AmountMinor = 9_99,
            Currency = "TRY",
            Customer = new LidioCustomer(orderId),
            ReturnUrl = SandboxBrowser.NewReturnUrl(),
            ClientIp = "127.0.0.1",
        }, _credentials, default);

        start.IsSuccess.ShouldBeTrue(start.ResultMessage);
        start.SystemTransId.ShouldNotBeNullOrEmpty();

        var returned = await browser.CompleteHostedPageAsync(start.RedirectUrl!, LidioTestCards.Garanti,
            LidioTestCards.GarantiOtp, output.WriteLine);
        output.WriteLine($"Ön ödeme dönüşü: {returned.Url}");

        // Kart ve 3D tamam ama para henüz çekilmedi:
        var before = await _client.PaymentInquiryAsync(orderId, _credentials, default);
        before.IsSuccess.ShouldBeFalse();

        var finish = await _client.FinishPaymentAsync(new LidioFinishPayment
        {
            OrderId = orderId,
            SystemTransId = returned.Fields.TryGetValue("SystemTransId", out var fromReturn) ? fromReturn : start.SystemTransId!,
            AmountMinor = 9_99,
            Currency = "TRY",
        }, _credentials, default);

        finish.IsSuccess.ShouldBeTrue($"{finish.Result} {finish.ResultDetail} {finish.ResultMessage}");
        finish.PaymentInfo!.AmountProcessed.ShouldBe(9.99m);

        (await _client.CancelAsync(orderId, _credentials, default)).IsSuccess.ShouldBeTrue();
    }

    [LidioSandboxFact]
    public async Task Stored_card_payment_with_3ds_finishes_after_bank_verification()
    {
        var customerId = LidioSandbox.NewCustomerId();
        var saved = await _client.SaveCardAsync(new LidioSaveCard
        {
            Customer = new LidioCustomer(customerId), Card = LidioTestCards.Garanti, ClientIp = "127.0.0.1",
        }, _credentials, default);
        saved.IsSuccess.ShouldBeTrue(saved.Result);

        var orderId = LidioSandbox.NewOrderId("p3s");
        var started = await _client.ProcessPaymentAsync(new LidioCardPayment
        {
            OrderId = orderId,
            AmountMinor = 6_50,
            Currency = "TRY",
            Customer = new LidioCustomer(customerId),
            StoredCardToken = saved.CardToken,
            Use3DSecure = true,
            ReturnUrl = SandboxBrowser.NewReturnUrl(),
            ClientIp = "127.0.0.1",
        }, _credentials, default);

        started.RequiresRedirect.ShouldBeTrue($"{started.Result} {started.ResultDetail}");
        var (actionUrl, fields) = ConnectorHtml.ExtractForm(started.RedirectForm!)
                                  ?? throw new ShouldAssertException("Lidio 3D formu ayrıştırılamadı.");

        var returned = await browser.CompleteThreeDsAsync(
            new HostedPaymentForm(actionUrl, fields), LidioTestCards.GarantiOtp, output.WriteLine);
        returned.Fields["Result"].ShouldBe("3DSuccess");

        var finish = await _client.FinishPaymentAsync(new LidioFinishPayment
        {
            OrderId = orderId,
            SystemTransId = started.PaymentInfo!.SystemTransId!,
            AmountMinor = 6_50,
            Currency = "TRY",
            StoredCard = true,
        }, _credentials, default);

        finish.IsSuccess.ShouldBeTrue($"{finish.Result} {finish.ResultDetail} {finish.ResultMessage}");
        finish.PaymentInfo!.Card!.MaskedCardNumber.ShouldBe("554960******0013");

        (await _client.CancelAsync(orderId, _credentials, default, storedCard: true)).IsSuccess.ShouldBeTrue();
        await _client.DeleteAllCardsOfCustomerAsync(customerId, _credentials, default);
    }

    [LidioSandboxFact]
    public async Task Hosted_account_management_page_opens_for_customer()
    {
        var customerId = LidioSandbox.NewCustomerId();
        await _client.SaveCardAsync(new LidioSaveCard
        {
            Customer = new LidioCustomer(customerId), Card = LidioTestCards.YapiKredi, ClientIp = "127.0.0.1",
        }, _credentials, default);

        var start = await _client.StartHostedAccountManagementAsync(
            new LidioCustomer(customerId), SandboxBrowser.NewReturnUrl(), threeDSecure: false, _credentials, default);

        start.IsSuccess.ShouldBeTrue(start.ResultMessage);
        start.RedirectUrl.ShouldStartWith("https://test.lidio.com/");

        // Sayfa müşterinin saklı kartını (maskeli) göstermeli — kart verisi Poyra'ya hiç gelmeden.
        var text = await browser.ReadPageTextAsync(start.RedirectUrl!, waitForText: "1809");
        output.WriteLine(text.Length > 600 ? text[..600] : text);
        text.ShouldContain("1809");

        await _client.DeleteAllCardsOfCustomerAsync(customerId, _credentials, default);
    }

    private async Task<HostedPaymentForm> StartThreeDsAsync(string attemptId, long amountMinor)
    {
        var form = await _connector.InitiateThreeDsDirectAsync(
            new DirectPaymentRequest(attemptId, amountMinor, "TRY", 1, LidioTestCards.Garanti, "Sandbox 3DS", "127.0.0.1"),
            SandboxBrowser.NewReturnUrl(), _credentials, default);

        form.ShouldNotBeNull();
        form!.ConnectorState!["poyra_lidio_attempt_id"].ShouldBe(attemptId);
        form.ConnectorState["poyra_lidio_order_id"].ShouldBe(LidioMessages.OrderId(attemptId));
        return form;
    }
}

/// <summary>Poyra callback işleyicisinin birleştirme kuralı: önce konnektör durumu, üstüne tarayıcının alanları.</summary>
public static class PoyraCallback
{
    public static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string>? state, IReadOnlyDictionary<string, string> returned)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in state ?? new Dictionary<string, string>())
            merged[key] = value;
        foreach (var (key, value) in returned)
            merged[key] = value; // bankanın dediği kazanır
        return merged;
    }
}
