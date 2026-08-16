using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.InterVpos;

/// <summary>
/// Denizbank <b>InterVPOS</b> — 3D Pay barındırılan akış: kart bankanın sayfasında girilir,
/// tahsilat tek adımda tamamlanır. PCI kapsamı minimaldir (kart verisi bize hiç uğramaz).
///
/// <b>⚠ SERTİFİKASYON DURUMU: bu adaptör bankanın entegrasyon dokümanından YAZILMADI.</b>
/// Genel InterVPOS desenine göre kuruldu; alan adları, hash sırası ve dönüş kodları
/// <b>bankanın dokümanıyla doğrulanmadan canlıya çıkamaz</b>. Mevcut dört adaptörden
/// (NestPay, GVP, Posnet, PayFlex) farkı budur ve karıştırılmamalıdır: onlar dokümandan
/// yazıldı, bu desenden. Her belirsiz nokta <c>TODO(cert)</c> ile işaretli.
///
/// <b>İptal/iade:</b> ayrı bir servis DEĞİL — aynı gateway'e <c>SecureType=NonSecure</c>
/// ve <c>TxnType=Void/Refund</c> ile sunucudan sunucuya form gönderilir.
/// </summary>
public sealed class InterVposConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string HttpClientName = "poyra-intervpos";

    public const string ConnectorKey = "intervpos";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Denizbank InterVPOS (3D Pay) — SERTİFİKASYON BEKLİYOR",
        ConnectorType.BankVirtualPos,
        [
            new CredentialField("gateway_base", "Gateway adresi (bankadan alınır)"),
            new CredentialField("shop_code", "Üye işyeri no (ShopCode)"),
            new CredentialField("merchant_pass", "İşyeri şifresi", Secret: true),
            new CredentialField("user_code", "Kullanıcı kodu (UserCode) — iptal/iade için zorunlu", Required: false),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "3D Pay: kart bankada girilir. İptal/iade aynı gateway'e NonSecure + "
               + "TxnType=Void/Refund ile gider. TODO(cert): alan adları, hash sırası ve "
               + "iptal/iade başarı alanı banka dokümanıyla doğrulanmadan canlıya alınmamalıdır.");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var gatewayBase = credentials.Require("gateway_base").TrimEnd('/');
        var shopCode = credentials.Require("shop_code");
        var merchantPass = credentials.Require("merchant_pass");

        var amount = InterVposMessages.Amount(request.AmountMinor);
        var installment = InterVposMessages.Installment(request.Installments);

        // Rastgele değer hash'in tekrar saldırısına kapatılması içindir; her istekte
        // yeni üretilir ve dönüşte aynısıyla doğrulanır
        var random = Guid.CreateVersion7().ToString("N");

        var hash = InterVposMessages.RequestHash(
            shopCode, request.OrderId, amount, request.CallbackUrl, request.CallbackUrl,
            "Auth", installment, random, merchantPass);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ShopCode"] = shopCode,
            ["PurchAmount"] = amount,
            ["Currency"] = InterVposMessages.TryCurrencyCode,
            ["OrderId"] = request.OrderId,
            ["OkUrl"] = request.CallbackUrl,
            ["FailUrl"] = request.CallbackUrl,
            ["TxnType"] = "Auth",
            ["SecureType"] = "3DPay",
            ["InstallmentCount"] = installment,
            ["Rnd"] = random,
            ["Hash"] = hash,
            ["Lang"] = "TR",
        };

        return Task.FromResult(new HostedPaymentForm(
            $"{gatewayBase}/VposWeb/v3/Vposreq.aspx",
            fields,
            // Rastgele değeri DÖNÜŞTE doğrulayabilmek için saklarız. Tarayıcıya
            // emanet edilseydi kurcalanır ve doğrulama anlamsızlaşırdı.
            ConnectorState: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rnd"] = random,
            }));
    }

    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var orderId = form.GetValueOrDefault("OrderId", string.Empty);
        var procReturnCode = form.GetValueOrDefault("ProcReturnCode");
        var response = form.GetValueOrDefault("Response", string.Empty);

        // Önce HASH: doğrulanmayan bir dönüş, tarayıcıdan gelen sahte bir
        //    "onaylandı" POST'unun tahsilat sayılması demektir.
        var random = form.GetValueOrDefault("rnd") ?? form.GetValueOrDefault("Rnd") ?? string.Empty;
        var expected = InterVposMessages.CallbackHash(
            credentials.Require("shop_code"), orderId, procReturnCode ?? string.Empty,
            response, random, credentials.Require("merchant_pass"));

        var provided = form.GetValueOrDefault("Hash");

        // Hash'in YOKLUĞU da reddedilir. Önceden yalnız "hash var ama tutmuyor" hâli
        // yakalanıyordu; alanı hiç göndermeyen bir POST doğrulamayı tamamen atlıyordu ve
        // callback adresini bilen herkes "Response=Approved" yazıp parası gelmemiş bir
        // siparişi tahsil edilmiş gösterebiliyordu.
        //
        // TODO(cert): banka dönüşte "Hash" alanını her senaryoda gönderiyor mu, hangi
        // alanları hash'liyor — dokümanla teyit edilmeli. Teyide kadar güvenli taraf
        // REDDETMEKTİR: imzasız dönüş ödemeyi askıda bırakır (operasyon telafi eder),
        // kabul etmek ise parayı hiç almadan "ödendi" yazar.
        if (string.IsNullOrEmpty(provided) || !string.Equals(provided, expected, StringComparison.Ordinal))
        {
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, provided, "Callback hash doğrulanamadı.");
        }

        if (!InterVposMessages.IsApproved(form))
        {
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                InterVposMessages.UnifiedError(procReturnCode),
                procReturnCode, form.GetValueOrDefault("ErrMsg"));
        }

        return new HostedCallbackResult(
            true, orderId,
            form.GetValueOrDefault("AuthCode"),
            form.GetValueOrDefault("HostRefNum") ?? form.GetValueOrDefault("TransId"),
            form.GetValueOrDefault("MaskedPan"),
            null,
            UnifiedErrors.None, procReturnCode, null);
    }

    /// <summary>
    /// İptal: gün sonu öncesi satışın geri alınması (<c>TxnType=Void</c>).
    /// Tutar gönderilmez — işlemin tamamı geri alınır.
    /// </summary>
    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, "Void", reference.OrderId, tutar: null, reference.ConnectorTxnId, ct);

    /// <summary>İade: gün sonu sonrası (kısmi) geri ödeme (<c>TxnType=Refund</c>).</summary>
    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => IslemAsync(credentials, "Refund", request.OrderId,
            InterVposMessages.Amount(request.AmountMinor), request.ConnectorTxnId, ct);

    /// <summary>
    /// İptal/iade sunucudan sunucuyadır ve 3D değildir (<c>SecureType=NonSecure</c>):
    /// işlem zaten yetkilendirilmiş, geri alınıyor.
    ///
    /// <b>TODO(cert):</b> başarı ölçütü olarak <c>ProcReturnCode=00</c> alındı. Banka
    /// bu uçta farklı bir alan döndürüyorsa iade BAŞARISIZ görünür (fail closed) —
    /// yanlış tarafa düşüp "iade edildi" demesindense böylesi yeğdir.
    /// </summary>
    private async Task<ConnectorOperationResult> IslemAsync(
        ConnectorCredentials credentials, string txnType, string orderId, string? tutar,
        string? txnId, CancellationToken ct)
    {
        var alanlar = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ShopCode"] = credentials.Require("shop_code"),
            ["UserCode"] = credentials.Require("user_code"),
            ["UserPass"] = credentials.Require("merchant_pass"),
            // Geri alınacak işlem ORİJİNAL sipariş numarasıyla gösterilir.
            ["orgOrderId"] = orderId,
            ["TxnType"] = txnType,
            ["SecureType"] = "NonSecure",
            ["Lang"] = "tr",
        };

        if (tutar is not null)
            alanlar["PurchAmount"] = tutar;

        var adres = credentials.Require("gateway_base").TrimEnd('/');

        try
        {
            var istemci = httpClientFactory.CreateClient(HttpClientName);
            using var yanit = await istemci.PostAsync(adres, new FormUrlEncodedContent(alanlar), ct);
            var metin = await yanit.Content.ReadAsStringAsync(ct);

            if (!yanit.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"InterVPOS {txnType} → {(int)yanit.StatusCode}.");

            var okunan = InterVposMessages.Oku(metin);
            var kod = okunan.GetValueOrDefault("ProcReturnCode");

            return kod == "00"
                ? ConnectorOperationResult.Ok(okunan.GetValueOrDefault("TransId") ?? txnId)
                : ConnectorOperationResult.Fail(
                    InterVposMessages.UnifiedError(kod), kod,
                    okunan.GetValueOrDefault("ErrorMessage") ?? okunan.GetValueOrDefault("ErrMsg"));
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"InterVPOS {txnType} ucuna ulaşılamadı.", ex);
        }
    }
}
