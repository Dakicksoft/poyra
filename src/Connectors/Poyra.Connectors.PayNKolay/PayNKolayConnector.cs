using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.PayNKolay;

/// <summary>
/// <b>PayNKolay</b> (Nkolay İşlem) ödeme kuruluşu.
///
/// Dönüş İMZALIDIR (<c>hashDataV2</c>) ve imza tutarı, taksiti ve sonuç kodunu kapsar —
/// kurcalanmış bir tutar imzayı düşürür. Bu yüzden PSP'lerin çoğundan farklı olarak
/// ayrı bir sunucu teyidi gerekmez; imza doğrulanır ve sonuç okunur.
///
/// <b>Dikkat:</b> dönüşteki <c>rnd</c> bizim gönderdiğimiz değil, sağlayıcının ürettiği
/// değerdir — istek ve dönüş hash'leri farklı alan listeleri kullanır.
///
/// <b>⚠ SERTİFİKASYON DURUMU:</b> alan adları ve durum kodları canlı hesapla
/// doğrulanmadan üretime alınmamalı.
/// </summary>
public sealed class PayNKolayConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "paynkolay";
    public const string HttpClientName = "poyra-paynkolay";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "PayNKolay — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "Servis adresi (ör. https://paynkolay.nkolayislem.com.tr)"),
            new CredentialField("sx", "Üye işyeri kimliği (sx)"),
            new CredentialField("secret_key", "İşyeri gizli anahtarı", Secret: true),
            new CredentialField("merchant_password", "İptal/iade şifresi", Secret: true, Required: false),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Dönüş hashDataV2 ile imzalıdır ve imza TUTARI kapsar; ayrı sunucu teyidi "
               + "gerekmez. Başarı kodu 2'dir ('00' değil). TODO(cert).");

    public Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var sx = credentials.Require("sx");
        var amount = PayNKolayMessages.Amount(request.AmountMinor);
        var random = PayNKolayMessages.RandomNonce();

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sx"] = sx,
            ["clientRefCode"] = request.OrderId,
            ["amount"] = amount,
            ["successUrl"] = request.CallbackUrl,
            ["failUrl"] = request.CallbackUrl,
            ["rnd"] = random,
            ["customerKey"] = string.Empty, // kart saklama kullanılmıyor
            ["installmentNo"] = Math.Max(1, request.Installments).ToString(),
            ["transactionType"] = "sales",
            ["use3D"] = "true",
            ["currencyNumber"] = "949",
            ["environment"] = "PROD",
            ["cardHolderIP"] = request.CustomerIp ?? "0.0.0.0",
            ["hashDatav2"] = PayNKolayMessages.RequestHash(
                sx, request.OrderId, amount, request.CallbackUrl, request.CallbackUrl,
                random, string.Empty, credentials.Require("secret_key")),
        };

        var url = $"{credentials.Require("gateway_base").TrimEnd('/')}/Vpos/v1/Payment";
        return Task.FromResult(new HostedPaymentForm(url, fields));
    }

    /// <summary>
    /// ÖNCE imza. İmzasız bir "RESPONSE_CODE=2" POST'unu tahsilat saymak, callback
    /// adresini gören müşterinin bedava sipariş yaratması demektir. İmza tutarı da
    /// kapsadığı için, ödenen tutarın değiştirilmesi de yakalanır.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
    {
        var orderId = form.GetValueOrDefault("CLIENT_REFERENCE_CODE", string.Empty);
        var code = form.GetValueOrDefault("RESPONSE_CODE");

        var expected = PayNKolayMessages.ResponseHash(
            credentials.Require("sx"),
            form.GetValueOrDefault("REFERENCE_CODE"),
            form.GetValueOrDefault("AUTH_CODE"),
            code,
            form.GetValueOrDefault("USE_3D"),
            form.GetValueOrDefault("RND"),
            form.GetValueOrDefault("INSTALLMENT"),
            form.GetValueOrDefault("AUTHORIZATION_AMOUNT"),
            form.GetValueOrDefault("CURRENCY_CODE"),
            credentials.Require("secret_key"));

        if (!PayNKolayMessages.IsSignatureValid(form.GetValueOrDefault("hashDataV2"), expected))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                UnifiedErrors.SignatureInvalid, code,
                "PayNKolay dönüş imzası (hashDataV2) doğrulanamadı.");

        if (!PayNKolayMessages.IsApproved(code))
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                PayNKolayMessages.UnifiedError(code), code,
                form.GetValueOrDefault("RESPONSE_DATA") ?? form.GetValueOrDefault("ERROR_MESSAGE"));

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: form.GetValueOrDefault("AUTH_CODE"),
            ConnectorTxnId: form.GetValueOrDefault("REFERENCE_CODE"),
            MaskedPan: form.GetValueOrDefault("MASKED_PAN"),
            CardBank: form.GetValueOrDefault("BANK_NAME"),
            UnifiedErrors.None, code, null);
    }

    public Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
        => CancelOrRefundAsync(credentials, "cancel", reference.ConnectorTxnId, string.Empty, ct);

    public Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
        => CancelOrRefundAsync(credentials, "refund", request.ConnectorTxnId,
            PayNKolayMessages.Amount(request.AmountMinor), ct);

    private async Task<ConnectorOperationResult> CancelOrRefundAsync(
        ConnectorCredentials credentials, string kind, string? reference, string amount, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sx"] = credentials.Require("sx"),
            ["referenceCode"] = reference ?? string.Empty,
            ["type"] = kind,
            ["amount"] = amount,
            ["trxDate"] = string.Empty,
        };

        var url = $"{credentials.Require("gateway_base").TrimEnd('/')}/Vpos/v1/CancelRefundPayment";

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(url, new FormUrlEncodedContent(fields), ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ConnectorUnavailableException($"PayNKolay {kind} → {(int)response.StatusCode}.");

            // Yanıt "RESPONSE_CODE=2" içeriyorsa onaylandı; biçim sağlayıcıya göre
            // değişebildiği için ham metinde aranır (TODO(cert): şema sabitlenecek).
            return text.Contains("\"RESPONSE_CODE\":\"2\"", StringComparison.Ordinal)
                   || text.Contains("RESPONSE_CODE=2", StringComparison.Ordinal)
                ? ConnectorOperationResult.Ok(reference)
                : ConnectorOperationResult.Fail(UnifiedErrors.ProcessingError, null, text[..Math.Min(200, text.Length)]);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"PayNKolay {kind} ucuna ulaşılamadı.", ex);
        }
    }
}
