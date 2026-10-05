using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Lidio;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Poyra.Tests.Sandbox;

/// <summary>
/// Satış, iptal, tam/kısmi iade ve ön provizyon — Lidio test ortamına GERÇEK çağrılar.
/// Tarayıcı gerekmez (3D'siz). Para hareketinin sonucu her testte ayrıca
/// <c>PaymentInquiry</c> ile sunucudan okunur: "Success" yanıtına değil kayda bakılır.
/// </summary>
[Collection(LidioSandboxCollection.Name)]
public sealed class LidioPaymentSandboxTests(ITestOutputHelper output)
{
    private readonly LidioConnector _connector = LidioSandbox.Connector();
    private readonly LidioClient _client = LidioSandbox.Client();
    private readonly ConnectorCredentials _credentials = LidioSandbox.Credentials;

    [LidioSandboxFact]
    public async Task Probe_reports_sandbox_healthy()
    {
        var probe = await _connector.ProbeAsync(_credentials, default);

        probe!.Healthy.ShouldBeTrue(probe.Detail);
        output.WriteLine(probe.Detail);
    }

    [LidioSandboxFact]
    public async Task Probe_with_wrong_api_key_is_unhealthy()
    {
        var wrong = new ConnectorCredentials(new Dictionary<string, string>(_credentials.Values)
        {
            ["api_key"] = Convert.ToBase64String("yanlis:anahtar"u8.ToArray()),
        });

        (await _connector.ProbeAsync(wrong, default))!.Healthy.ShouldBeFalse();
    }

    [LidioSandboxFact]
    public async Task Direct_sale_without_3ds_then_void_reverses_it()
    {
        var attemptId = LidioSandbox.NewAttemptId();
        var orderId = LidioMessages.OrderId(attemptId);

        var sale = await SellAsync(attemptId, 10_50);

        sale.Success.ShouldBeTrue(sale.RawMessage);
        sale.AuthCode.ShouldNotBeNullOrEmpty();
        sale.MaskedPan.ShouldBe("554960******0013");
        sale.ConnectorTxnId.ShouldNotBeNullOrEmpty();

        var inquiry = await _client.PaymentInquiryAsync(orderId, _credentials, default);
        inquiry.IsSuccess.ShouldBeTrue();
        inquiry.PaymentInfo!.OrderId.ShouldBe(orderId); // 36 karakterlik kimlik 20'ye indi
        inquiry.PaymentInfo.AmountProcessed.ShouldBe(10.50m);
        inquiry.PaymentInfo.Card!.Is3DSecure.ShouldBe(false);

        var voided = await _connector.VoidAsync(new ConnectorReference(attemptId, sale.ConnectorTxnId), _credentials, default);
        voided.Success.ShouldBeTrue(voided.RawMessage);
        voided.ConnectorTxnId.ShouldNotBe(sale.ConnectorTxnId); // iptalin kendi işlem numarası

        var cancelled = await _client.PaymentInquiryAsync(orderId, _credentials, default, LidioProcessTypes.Cancel);
        cancelled.IsSuccess.ShouldBeTrue();
        cancelled.PaymentInfo!.Card!.RefTransType.ShouldBe("sales");

        // İkinci iptal: Lidio "zaten iptal/iade edildi" der — başarı SAYILMAMALI.
        var again = await _connector.VoidAsync(new ConnectorReference(attemptId, sale.ConnectorTxnId), _credentials, default);
        again.Success.ShouldBeFalse();
        again.RawCode.ShouldBe("RefTransactionNotFound");
    }

    [LidioSandboxFact]
    public async Task Partial_refunds_then_remainder_then_over_refund_is_refused()
    {
        var attemptId = LidioSandbox.NewAttemptId();
        var sale = await SellAsync(attemptId, 20_00);
        sale.Success.ShouldBeTrue(sale.RawMessage);

        // 1. kısmi iade (iade kimliğiyle)
        var firstRefundId = "ref_" + Guid.CreateVersion7().ToString("N");
        var first = await _connector.RefundAsync(
            new ConnectorRefundRequest(attemptId, sale.ConnectorTxnId, 5_00, "TRY", firstRefundId), _credentials, default);
        first.Success.ShouldBeTrue(first.RawMessage);

        // Aynı iade kimliğiyle tekrar (ör. zaman aşımı sonrası): ikinci iade DOĞMAMALI.
        var retried = await _connector.RefundAsync(
            new ConnectorRefundRequest(attemptId, sale.ConnectorTxnId, 5_00, "TRY", firstRefundId), _credentials, default);
        retried.Success.ShouldBeTrue("DuplicateRequest daha önce yapılmış iadenin teyididir");

        // 2. kısmi iade: AYNI tutar, farklı kimlik — ayrı bir iadedir.
        var second = await _connector.RefundAsync(
            new ConnectorRefundRequest(attemptId, sale.ConnectorTxnId, 5_00, "TRY", "ref_" + Guid.CreateVersion7().ToString("N")),
            _credentials, default);
        second.Success.ShouldBeTrue(second.RawMessage);

        // Kalandan fazlası (10,01 > 10,00) reddedilir.
        var over = await _connector.RefundAsync(
            new ConnectorRefundRequest(attemptId, sale.ConnectorTxnId, 10_01, "TRY"), _credentials, default);
        over.Success.ShouldBeFalse();
        output.WriteLine($"Fazla iade: {over.RawCode} / {over.UnifiedCode}");

        // Kalanın tamamı iade edilir.
        var rest = await _connector.RefundAsync(
            new ConnectorRefundRequest(attemptId, sale.ConnectorTxnId, 10_00, "TRY"), _credentials, default);
        rest.Success.ShouldBeTrue(rest.RawMessage);

        // Kayıt: toplam iade satış tutarına eşit — mükerrer çağrı tutara eklenmedi.
        var refunds = await _client.PaymentInquiryAsync(
            LidioMessages.OrderId(attemptId), _credentials, default, LidioProcessTypes.Refund);
        refunds.TotalRefund.ShouldBe(20.00m);

        // Artık iade edilecek bir şey kalmadı.
        var nothingLeft = await _connector.RefundAsync(
            new ConnectorRefundRequest(attemptId, sale.ConnectorTxnId, 1_00, "TRY"), _credentials, default);
        nothingLeft.Success.ShouldBeFalse();
    }

    [LidioSandboxFact]
    public async Task Full_refund_in_one_call()
    {
        var attemptId = LidioSandbox.NewAttemptId();
        var sale = await SellAsync(attemptId, 7_77);
        sale.Success.ShouldBeTrue(sale.RawMessage);

        var refund = await _connector.RefundAsync(
            new ConnectorRefundRequest(attemptId, sale.ConnectorTxnId, 7_77, "TRY"), _credentials, default);

        refund.Success.ShouldBeTrue(refund.RawMessage);
        (await _client.PaymentInquiryAsync(LidioMessages.OrderId(attemptId), _credentials, default, LidioProcessTypes.Refund))
            .TotalRefund.ShouldBe(7.77m);
    }

    [LidioSandboxFact]
    public async Task Preauth_then_partial_postauth_then_refund()
    {
        var orderId = LidioSandbox.NewOrderId("pa");

        var preauth = await _client.ProcessPaymentAsync(new LidioCardPayment
        {
            OrderId = orderId, AmountMinor = 50_00, Currency = "TRY", Customer = new LidioCustomer(orderId),
            ProcessType = LidioProcessTypes.PreAuth, NewCard = LidioTestCards.Garanti, ClientIp = "127.0.0.1",
        }, _credentials, default);
        preauth.IsSuccess.ShouldBeTrue($"{preauth.Result} {preauth.ResultDetail}");
        preauth.PaymentInfo!.Card!.ProcessType.ShouldBe("preauth");

        // Provizyondan küçük tutarla kapama (ör. eksik teslimat).
        var postauth = await _client.PostauthAsync(orderId, 40_00, "TRY", _credentials, default);
        postauth.IsSuccess.ShouldBeTrue($"{postauth.Result} {postauth.ResultDetail} {postauth.ResultMessage}");
        postauth.PaymentInfo!.AmountProcessed.ShouldBe(40.00m);

        var closed = await _client.PaymentInquiryAsync(orderId, _credentials, default, LidioProcessTypes.PostAuth);
        closed.IsSuccess.ShouldBeTrue();

        var refund = await _client.RefundAsync(orderId, 10_00, "TRY", null, _credentials, default);
        refund.IsSuccess.ShouldBeTrue($"{refund.Result} {refund.ResultDetail}");
    }

    [LidioSandboxFact]
    public async Task Preauth_can_be_cancelled_before_capture()
    {
        var orderId = LidioSandbox.NewOrderId("pc");

        var preauth = await _client.ProcessPaymentAsync(new LidioCardPayment
        {
            OrderId = orderId, AmountMinor = 25_00, Currency = "TRY", Customer = new LidioCustomer(orderId),
            ProcessType = LidioProcessTypes.PreAuth, NewCard = LidioTestCards.Garanti,
        }, _credentials, default);
        preauth.IsSuccess.ShouldBeTrue(preauth.Result);

        var cancel = await _client.CancelAsync(orderId, _credentials, default);
        cancel.IsSuccess.ShouldBeTrue($"{cancel.Result} {cancel.ResultDetail}");

        // Kapanmış provizyon artık kapatılamaz.
        (await _client.PostauthAsync(orderId, 25_00, "TRY", _credentials, default)).IsSuccess.ShouldBeFalse();
    }

    [LidioSandboxFact]
    public async Task Wrong_cvv_is_declined_as_invalid_card()
    {
        var result = await _connector.AuthorizeDirectAsync(
            new DirectPaymentRequest(LidioSandbox.NewAttemptId(), 3_00, "TRY", 1,
                LidioTestCards.Garanti with { Cvv = "111" }, null, "127.0.0.1"),
            _credentials, default);

        result!.Success.ShouldBeFalse();
        result.UnifiedCode.ShouldBe(UnifiedErrors.InvalidCard); // LD03 — hatalı CVV
        output.WriteLine($"{result.RawCode} / {result.RawMessage}");
    }

    [LidioSandboxFact]
    public async Task Expired_card_is_rejected_before_bank()
    {
        var result = await _connector.AuthorizeDirectAsync(
            new DirectPaymentRequest(LidioSandbox.NewAttemptId(), 3_00, "TRY", 1,
                LidioTestCards.Garanti with { ExpiryYear = 2020 }, null, "127.0.0.1"),
            _credentials, default);

        result!.Success.ShouldBeFalse();
        result.RawCode.ShouldBe("InvalidYear");
        result.UnifiedCode.ShouldBe(UnifiedErrors.ExpiredCard);
    }

    [LidioSandboxFact]
    public async Task Same_order_id_cannot_be_charged_twice()
    {
        // Lidio başarılı satışta sipariş no tekilliğini zorlar; çift tıklama çift çekim olmaz.
        var attemptId = LidioSandbox.NewAttemptId();
        (await SellAsync(attemptId, 2_00)).Success.ShouldBeTrue();

        var duplicate = await SellAsync(attemptId, 2_00);

        duplicate.Success.ShouldBeFalse();
        output.WriteLine($"Çift satış: {duplicate.RawCode} / {duplicate.RawMessage}");
        await _connector.VoidAsync(new ConnectorReference(attemptId, null), _credentials, default);
    }

    [LidioSandboxFact]
    public async Task Installment_info_lists_pos_and_single_payment_option()
    {
        var info = await _client.GetInstallmentInfoAsync("554960", 100_00, _credentials, default);

        info.IsSuccess.ShouldBeTrue(info.Result);
        var posList = info.PosList.ShouldNotBeNull();
        posList.ShouldNotBeEmpty();
        foreach (var pos in posList)
            output.WriteLine($"POS {pos.PosId} {pos.PosBankName} [{pos.PosCardPrograms}] — taksitler: "
                             + string.Join(", ", pos.InstallmentOptionList?.Select(o => o.InstallmentCount) ?? []));

        // Tek çekim Lidio'da 0 taksittir ve vade farksızdır.
        posList.SelectMany(p => p.InstallmentOptionList ?? [])
            .ShouldContain(o => o.InstallmentCount == 0 && o.TotalAmountWithInterest == 100.00m);
    }

    [LidioSandboxFact]
    public async Task Bin_inquiry_is_answered_or_reported_as_not_authorized()
    {
        // GetBankOfBINNumber Lidio'da ayrıca açılan bir hizmettir.
        var bin = await _client.GetBankOfBinNumberAsync("554960", _credentials, default);

        if (bin.IsNotAuthorized)
        {
            output.WriteLine("YETKİ YOK: GetBankOfBINNumber bu test hesabında açık değil (InvalidCredential).");
            return;
        }

        bin.IsSuccess.ShouldBeTrue(bin.Result);
        bin.BankCode.ShouldBe("062");
    }

    private async Task<DirectAuthorizeResult> SellAsync(string attemptId, long amountMinor)
        => (await _connector.AuthorizeDirectAsync(
            new DirectPaymentRequest(attemptId, amountMinor, "TRY", 1, LidioTestCards.Garanti, "Sandbox satış", "127.0.0.1"),
            _credentials, default))!;
}
