using Poyra.Connectors.Abstractions;
using Poyra.Connectors.Lidio;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Poyra.Tests.Sandbox;

/// <summary>
/// Lidio saklı kart (StoredCard) metotları — test ortamına GERÇEK çağrılar. Her test kendi
/// müşterisini açar ve sonunda o müşterinin kartlarını siler; test hesabında kart birikmez.
/// </summary>
[Collection(LidioSandboxCollection.Name)]
public sealed class LidioStoredCardSandboxTests(ITestOutputHelper output)
{
    private readonly LidioClient _client = LidioSandbox.Client();
    private readonly ConnectorCredentials _credentials = LidioSandbox.Credentials;

    [LidioSandboxFact]
    public async Task Card_lifecycle_save_list_inquire_update_delete()
    {
        var customer = new LidioCustomer(LidioSandbox.NewCustomerId(), "sandbox@poyra.test");

        var saved = await SaveAsync(customer, LidioTestCards.Garanti, "İş kartı");
        saved.CardNamebyUser.ShouldBe("İş kartı");

        var list = await _client.GetCardListAsync(customer.CustomerId, _credentials, default);
        list.IsSuccess.ShouldBeTrue(list.Result);
        var card = list.CardList.ShouldNotBeNull().ShouldHaveSingleItem();
        card.CardToken.ShouldBe(saved.CardToken);
        card.MaskedCardNumber.ShouldBe("554960******0013");
        card.BankCode.ShouldBe("062");
        card.CardHolderName.ShouldNotBe("POYRA TEST"); // Lidio sahip adını maskeli döner

        var info = await _client.TokenToCardInfoInquiryAsync(customer.CustomerId, saved.CardToken!, _credentials, default);
        info.IsSuccess.ShouldBeTrue(info.Result);
        info.CustomerId.ShouldBe(customer.CustomerId);
        info.CardInfo!.IsExpired.ShouldBe(false);

        // Değişiklik olmayan güncelleme Lidio'da DuplicateCard'dır; tarih değişince Success.
        var updated = await _client.UpdateCardInfoAsync(new LidioUpdateCard
        {
            Customer = customer,
            CardToken = saved.CardToken!,
            ExpiryMonth = 3,
            ExpiryYear = 2031,
            CardNameByUser = "Yenilenen kart",
            SetAsDefault = true,
            ClientIp = "127.0.0.1",
        }, _credentials, default);
        updated.IsSuccess.ShouldBeTrue(updated.Result);
        updated.CardNamebyUser.ShouldBe("Yenilenen kart");

        var afterUpdate = (await _client.GetCardListAsync(customer.CustomerId, _credentials, default)).CardList!.Single();
        afterUpdate.CardNamebyUser.ShouldBe("Yenilenen kart");
        afterUpdate.IsDefault.ShouldBe(true);

        var deleted = await _client.DeleteCardAsync(customer.CustomerId, saved.CardToken!, _credentials, default);
        deleted.IsSuccess.ShouldBeTrue(deleted.Result);

        // Kartı kalmayan müşteri için Lidio boş liste değil CardNotFound döner.
        var empty = await _client.GetCardListAsync(customer.CustomerId, _credentials, default);
        empty.Result.ShouldBe("CardNotFound");
    }

    [LidioSandboxFact]
    public async Task Stored_card_payment_without_verification_then_refund()
    {
        var customer = new LidioCustomer(LidioSandbox.NewCustomerId());
        var saved = await SaveAsync(customer, LidioTestCards.Garanti);
        var orderId = LidioSandbox.NewOrderId("ps");

        var payment = await _client.ProcessPaymentAsync(new LidioCardPayment
        {
            OrderId = orderId, AmountMinor = 3_10, Currency = "TRY", Customer = customer,
            StoredCardToken = saved.CardToken, ClientIp = "127.0.0.1",
        }, _credentials, default);

        payment.IsSuccess.ShouldBeTrue($"{payment.Result} {payment.ResultDetail}");
        payment.PaymentInfo!.InstrumentType.ShouldBe("StoredCard");
        payment.PaymentInfo.AmountProcessed.ShouldBe(3.10m);
        payment.PaymentInfo.Pos!.AuthCode.ShouldNotBeNullOrEmpty();

        var refund = await _client.RefundAsync(orderId, 1_10, "TRY", null, _credentials, default, storedCard: true);
        refund.IsSuccess.ShouldBeTrue($"{refund.Result} {refund.ResultDetail}");

        await CleanupAsync(customer);
    }

    [LidioSandboxFact]
    public async Task Stored_card_payment_with_cvv_verification_requires_finish()
    {
        var customer = new LidioCustomer(LidioSandbox.NewCustomerId());
        var saved = await SaveAsync(customer, LidioTestCards.Garanti);
        var orderId = LidioSandbox.NewOrderId("pv");

        var started = await _client.ProcessPaymentAsync(new LidioCardPayment
        {
            OrderId = orderId, AmountMinor = 7_25, Currency = "TRY", Customer = customer,
            StoredCardToken = saved.CardToken, VerificationMethods = ["CVV"], ClientIp = "127.0.0.1",
        }, _credentials, default);

        // CVV ödeme isteğinde verilse bile ayrı doğrulama adımı istenir; para henüz çekilmedi.
        started.RequiresVerification.ShouldBeTrue($"{started.Result} {started.ResultDetail}");
        started.ResultDetail.ShouldBe("CVVRequired");
        (await _client.PaymentInquiryAsync(orderId, _credentials, default)).IsSuccess.ShouldBeFalse();

        var finish = await _client.FinishPaymentAsync(new LidioFinishPayment
        {
            OrderId = orderId,
            SystemTransId = started.PaymentInfo!.SystemTransId!,
            AmountMinor = 7_25,
            Currency = "TRY",
            StoredCard = true,
            Cvv = LidioTestCards.Garanti.Cvv,
        }, _credentials, default);

        finish.IsSuccess.ShouldBeTrue($"{finish.Result} {finish.ResultDetail} {finish.ResultMessage}");
        (await _client.PaymentInquiryAsync(orderId, _credentials, default)).IsSuccess.ShouldBeTrue();

        // İptal asıl ödemenin aracıyla yapılmalı — NewCard ile bu ödeme bulunamaz.
        (await _client.CancelAsync(orderId, _credentials, default)).ResultDetail.ShouldBe("RefTransactionNotFound");
        (await _client.CancelAsync(orderId, _credentials, default, storedCard: true)).IsSuccess.ShouldBeTrue();
        await CleanupAsync(customer);
    }

    [LidioSandboxFact]
    public async Task Card_is_saved_after_successful_payment_when_requested()
    {
        var customer = new LidioCustomer(LidioSandbox.NewCustomerId());
        var orderId = LidioSandbox.NewOrderId("pk");

        var payment = await _client.ProcessPaymentAsync(new LidioCardPayment
        {
            OrderId = orderId, AmountMinor = 2_00, Currency = "TRY", Customer = customer,
            NewCard = LidioTestCards.YapiKredi, SaveCardAfterSuccess = true, ClientIp = "127.0.0.1",
        }, _credentials, default);

        payment.IsSuccess.ShouldBeTrue($"{payment.Result} {payment.ResultDetail}");
        payment.PaymentInfo!.Card!.IsCardSaved.ShouldBe(true);
        var token = payment.PaymentInfo.Card.CardToken.ShouldNotBeNull();

        var list = await _client.GetCardListAsync(customer.CustomerId, _credentials, default);
        list.CardList.ShouldNotBeNull().ShouldContain(c => c.CardToken == token);

        (await _client.CancelAsync(orderId, _credentials, default)).IsSuccess.ShouldBeTrue();
        await CleanupAsync(customer);
    }

    [LidioSandboxFact]
    public async Task Temporary_card_can_be_saved_permanently_with_its_token()
    {
        var customer = new LidioCustomer(LidioSandbox.NewCustomerId());

        var temporary = await _client.SaveCardAsync(new LidioSaveCard
        {
            Customer = customer, Card = LidioTestCards.YapiKredi, IsTemporary = true, ClientIp = "127.0.0.1",
        }, _credentials, default);
        temporary.IsSuccess.ShouldBeTrue(temporary.Result);
        temporary.CardToken.ShouldNotBeNullOrEmpty();

        var permanent = await _client.SaveCardAsync(new LidioSaveCard
        {
            Customer = customer, TemporaryCardToken = temporary.CardToken, ClientIp = "127.0.0.1",
        }, _credentials, default);
        output.WriteLine($"Geçici → kalıcı: {permanent.Result} {permanent.CardToken}");
        permanent.IsSuccess.ShouldBeTrue(permanent.Result);

        (await _client.GetCardListAsync(customer.CustomerId, _credentials, default))
            .CardList.ShouldNotBeNull().ShouldContain(c => c.MaskedCardNumber == "450634******1809");

        await CleanupAsync(customer);
    }

    [LidioSandboxFact]
    public async Task Delete_all_cards_of_customer_clears_every_card()
    {
        var customer = new LidioCustomer(LidioSandbox.NewCustomerId());
        await SaveAsync(customer, LidioTestCards.Garanti);
        await SaveAsync(customer, LidioTestCards.YapiKredi);

        (await _client.GetCardListAsync(customer.CustomerId, _credentials, default)).CardList!.Count.ShouldBe(2);

        var cleared = await _client.DeleteAllCardsOfCustomerAsync(customer.CustomerId, _credentials, default);

        cleared.IsSuccess.ShouldBeTrue(cleared.Result);
        (await _client.GetCardListAsync(customer.CustomerId, _credentials, default)).Result.ShouldBe("CardNotFound");
    }

    [LidioSandboxFact]
    public async Task Deleting_unknown_card_is_not_success()
    {
        var result = await _client.DeleteCardAsync(LidioSandbox.NewCustomerId(), "00000000-0000-0000-0000-000000000000",
            _credentials, default);

        result.IsSuccess.ShouldBeFalse();
        output.WriteLine(result.Result);
    }

    /// <summary>
    /// Interoperable / telefon doğrulamalı mod ve ek yetki isteyen metotlar. Lidio bunları
    /// hesap düzeyinde açar; açık değilse <c>InvalidCredential</c> döner. Test, isteğin
    /// biçimsel olarak KABUL edildiğini (doğrulama hatası değil) ve sonucu raporlar —
    /// yetki açıldığında aynı test başarı yolunu sınar.
    /// </summary>
    [LidioSandboxFact]
    public async Task Account_gated_stored_card_methods_are_reachable()
    {
        var customer = new LidioCustomer(LidioSandbox.NewCustomerId());
        var saved = await SaveAsync(customer, LidioTestCards.Garanti);
        const string phone = "5551234567";

        var results = new List<(string Method, LidioResponse Response)>
        {
            ("CardToTokenInquiry", await _client.CardToTokenInquiryAsync(LidioTestCards.Garanti.Pan, _credentials, default)),
            ("CopyStoredCard", await _client.CopyStoredCardAsync(customer.CustomerId, customer.CustomerId + "k",
                saved.CardToken!, _credentials, default)),
            ("SendOTPForCardSave", await _client.SendOtpForCardSaveAsync(phone, _credentials, default, "127.0.0.1")),
            ("SendOTPForCardUpdate", await _client.SendOtpForCardUpdateAsync(phone, _credentials, default, "127.0.0.1")),
            ("SendOTPForCardRetrieve", await _client.SendOtpForCardRetrieveAsync(customer.CustomerId, phone,
                _credentials, default, "127.0.0.1")),
            ("RetrieveCards", await _client.RetrieveCardsAsync(customer.CustomerId, phone, "123456",
                _credentials, default, "127.0.0.1")),
            ("SendOTPforConsentUpdate", await _client.SendOtpForConsentUpdateAsync(customer.CustomerId, phone,
                _credentials, default, "127.0.0.1")),
            ("UpdateConsent", await _client.UpdateConsentAsync(customer.CustomerId, phone, null, "127.0.0.1",
                _credentials, default)),
        };

        foreach (var (method, response) in results)
        {
            output.WriteLine($"{method,-24} → {response.Result}{(response.IsNotAuthorized ? "  (hesapta yetki yok)" : "")}");
            response.Result.ShouldNotBeNullOrEmpty($"{method} sonuç kodu dönmedi");
            // Gövde biçimi bozuksa Lidio InvalidParameter + ayrıntı döner; o bizim hatamızdır.
            response.Result.ShouldNotBe("InvalidParameter", $"{method}: {response.ResultDetail} {response.ResultMessage}");
        }

        await CleanupAsync(customer);
    }

    private async Task<LidioSaveCardResponse> SaveAsync(LidioCustomer customer, CardData card, string? name = null)
    {
        var saved = await _client.SaveCardAsync(new LidioSaveCard
        {
            Customer = customer, Card = card, CardNameByUser = name, ClientIp = "127.0.0.1",
        }, _credentials, default);

        saved.IsSuccess.ShouldBeTrue($"SaveCard: {saved.Result} {saved.ResultMessage}");
        saved.CardToken.ShouldNotBeNullOrEmpty();
        return saved;
    }

    private Task CleanupAsync(LidioCustomer customer)
        => _client.DeleteAllCardsOfCustomerAsync(customer.CustomerId, _credentials, default);
}
