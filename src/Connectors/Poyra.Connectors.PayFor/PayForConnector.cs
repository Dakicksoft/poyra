using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.PayFor;

/// <summary>
/// <b>PayFor</b> sanal POS — QNB Finansbank'ın kendi altyapısı.
///
/// Bu konnektör diğer yeni eklenenlerden AYRILIR: 3DPay modelinde kart bankanın
/// sayfasında girilir, yani gerçek banka-hosted akıştır ve PCI kapsamı minimaldir.
/// Ödeme tek adımda tamamlanır; ayrı bir provizyon çağrısı gerekmez — çünkü dönüşün
/// kendisi <b>imzalıdır</b> (ResponseHash) ve imza MerchantPass'i bilmeden üretilemez.
///
/// <b>⚠ SERTİFİKASYON DURUMU:</b> alan adları ve hata kodları banka sertifikasyon
/// testlerinden geçmeden üretime alınmamalı.
/// </summary>
public sealed class PayForConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "payfor";
    public const string HttpClientName = "poyra-payfor";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "QNB Finansbank Sanal POS (PayFor, 3DPay) — SERTİFİKASYON BEKLİYOR",
        ConnectorType.BankVirtualPos,
        [
            new CredentialField("gateway_base", "Gateway adresi (ör. https://vpos.qnbfinansbank.com)"),
            new CredentialField("mbr_id", "Üye kodu (MbrId — genelde 5)"),
            new CredentialField("merchant_id", "Üye işyeri no (MerchantID)"),
            new CredentialField("user_code", "API kullanıcı kodu (UserCode)"),
            new CredentialField("user_pass", "API kullanıcı şifresi (UserPass)", Secret: true),
            new CredentialField("merchant_pass", "İşyeri anahtarı (MerchantPass / storekey)", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "3DPay: kart BANKANIN sayfasında girilir → PCI kapsamı minimal. Dönüş "
               + "ResponseHash ile imzalıdır, ayrı provizyon çağrısı gerekmez. İptal aynı "
               + "gün/batch içinde, iade farklı günde yapılır. TODO(cert).");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var mbrId = credentials.Require("mbr_id");
        var amount = PayForMessages.Amount(request.AmountMinor);
        var installment = request.Installments > 1 ? request.Installments.ToString() : string.Empty;
        var random = PayForMessages.RandomNonce();

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MbrId"] = mbrId,
            ["MerchantID"] = credentials.Require("merchant_id"),
            ["UserCode"] = credentials.Require("user_code"),
            // UserPass BİLEREK forma konmaz. Bu form müşterinin tarayıcısında görünür;
            // API şifresini oraya yazmak, müşteriye işyeri adına API çağırma imkânı
            // vermek olurdu. İsteği doğrulayan şey zaten Hash'tir ve o MerchantPass
            // olmadan üretilemez.
            // TODO(cert): banka 3DPay formunda UserPass İSTİYORSA bu bir protokol
            // kusurudur ve sertifikasyonda bankaya bildirilmeli — sessizce eklenmemeli.
            ["MOID"] = request.OrderId,
            ["OrderId"] = request.OrderId,
            ["SecureType"] = "3DPay", // kart bankanın sayfasında
            ["TxnType"] = "Auth",
            ["PurchAmount"] = amount,
            ["Currency"] = PayForMessages.Currency(request.Currency),
            // Tek çekimde BOŞ gider, "1" değil: "1" bazı bankalarda taksit kampanyası
            // sayılır ve işlem farklı komisyonla geçer.
            ["InstallmentCount"] = installment,
            ["OkUrl"] = request.CallbackUrl,
            ["FailUrl"] = request.CallbackUrl,
            ["Rnd"] = random,
            ["Lang"] = "TR",
            ["Hash"] = PayForMessages.RequestHash(
                mbrId, request.OrderId, amount, request.CallbackUrl, request.CallbackUrl,
                "Auth", installment, random, credentials.Require("merchant_pass")),
        };

        var url = $"{credentials.Require("gateway_base").TrimEnd('/')}/Gateway/Default.aspx";
        return Task.FromResult(new HostedPaymentForm(url, fields));
    }

    /// <summary>
    /// Dönüş İMZALIDIR: ResponseHash MerchantPass'i bilmeden üretilemez. Bu yüzden
    /// bu ailede — PSP'lerin aksine — ayrı bir sunucu teyidi gerekmez; imza doğrulanır
    /// ve sonuç doğrudan okunur.
    ///
    /// Sıralama önemli: ÖNCE imza. İmzasız bir "ProcReturnCode=00" POST'unu tahsilat
    /// saymak, callback adresini gören müşterinin bedava sipariş yaratması demektir.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var orderId = form.GetValueOrDefault("OrderId") ?? form.GetValueOrDefault("MOID", string.Empty);
        var procReturnCode = form.GetValueOrDefault("ProcReturnCode");
        var threeDStatus = form.GetValueOrDefault("3DStatus");

        var expected = PayForMessages.ResponseHash(
            credentials.Require("merchant_id"), credentials.Require("merchant_pass"),
            orderId, form.GetValueOrDefault("AuthCode"), procReturnCode, threeDStatus,
            form.GetValueOrDefault("ResponseRnd"), credentials.Require("user_code"));

        if (!PayForMessages.IsSignatureValid(form.GetValueOrDefault("ResponseHash"), expected))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, procReturnCode,
                "PayFor dönüş imzası (ResponseHash) doğrulanamadı.");

        if (procReturnCode != "00" || threeDStatus != "1")
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                PayForMessages.UnifiedError(procReturnCode, threeDStatus),
                procReturnCode, form.GetValueOrDefault("ErrMsg"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: form.GetValueOrDefault("AuthCode"),
            ConnectorTxnId: form.GetValueOrDefault("HostRefNum") ?? form.GetValueOrDefault("TransId"),
            MaskedPan: form.GetValueOrDefault("CardMask"),
            CardBank: "QNB Finansbank",
            UnifiedErrors.None, procReturnCode, null);
    }

    /// <summary>
    /// İptal (Void): satışın AYNI GÜN ya da aynı batch içinde geri alınması — kart
    /// ekstresinde hiç görünmez, komisyon doğmaz. Farklı gündeki geri alma iadedir.
    ///
    /// Bu çağrı sunucudan sunucuyadır, yani <c>UserPass</c> burada gönderilebilir —
    /// tarayıcıya giden 3D formunda gönderilemez (bkz. yukarıdaki not).
    /// </summary>
    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, "Void", reference.OrderId, amount: null, "TRY", ct);

    /// <summary>
    /// İade (Refund): satış günü GEÇTİKTEN sonra paranın geri gönderilmesi. Banka
    /// tutarı üç–yedi gün içinde karta yatırır.
    /// </summary>
    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => OperationAsync(credentials, "Refund", request.OrderId,
            PayForMessages.Amount(request.AmountMinor), request.Currency, ct);

    private async Task<ConnectorOperationResult> OperationAsync(
        ConnectorCredentials credentials, string txnType, string orderId, string? amount,
        string currency, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MbrId"] = credentials.Require("mbr_id"),
            ["MerchantId"] = credentials.Require("merchant_id"),
            ["UserCode"] = credentials.Require("user_code"),
            ["UserPass"] = credentials.Require("user_pass"),
            ["OrderId"] = orderId,
            ["TxnType"] = txnType,
            // İptal/iade 3D değil: işlem zaten yetkilendirilmiş, geri alınıyor.
            ["SecureType"] = "NonSecure",
            ["Currency"] = PayForMessages.Currency(currency),
            ["Lang"] = "TR",
        };

        // Tutar YALNIZ iadede gönderilir ve tam iki ondalık olmalıdır — banka
        // "99,500" gibi üç ondalıklı değeri reddediyor.
        if (amount is not null)
            fields["PurchAmount"] = amount;

        var url = $"{credentials.Require("gateway_base").TrimEnd('/')}/Gateway/Default.aspx";

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(url, new FormUrlEncodedContent(fields), ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"PayFor {txnType} → {(int)response.StatusCode}.");

            var responseFields = PayForMessages.Parse(text);
            var code = responseFields.GetValueOrDefault("ProcReturnCode");

            return code == "00"
                ? ConnectorOperationResult.Ok(responseFields.GetValueOrDefault("TransId"))
                : ConnectorOperationResult.Fail(
                    PayForMessages.UnifiedError(code, null), code,
                    responseFields.GetValueOrDefault("ErrMsg"));
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"PayFor {txnType} ucuna ulaşılamadı.", ex);
        }
    }
}
