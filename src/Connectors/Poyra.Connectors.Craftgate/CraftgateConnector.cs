using System.Globalization;
using System.Text;
using System.Text.Json;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Craftgate;

/// <summary>
/// <b>Craftgate</b> ödeme orkestrasyonu — imzalı JSON REST.
///
/// İki akışı da destekler:
/// <list type="bullet">
/// <item>Ortak Ödeme Sayfası (hosted) — kart Craftgate'te girilir, <b>PCI kapsamı dışı</b>.</item>
/// <item>3DS'li direct — kart bizim formumuzda toplanır, <b>PCI kapsamı içi</b>.</item>
/// </list>
///
/// <b>Dönüş doğrulaması:</b> her iki akışta da tarayıcı dönüşü kanıt DEĞİLDİR ve
/// sonuç sunucudan okunur. Kritik nokta şu: dönüşü sorgulamak için gereken kimlik
/// (ortak sayfada <c>token</c>, direct'te <c>paymentId</c>) tarayıcıya emanet edilmez —
/// başlatma yanıtından alınıp KONNEKTÖR DURUMU olarak saklanır. Craftgate aynı adları
/// dönüşte de POST'lar; durum anahtarlarımız <c>poyra_</c> önekli olduğu için tarayıcının
/// gönderdiği değerler bizimkilerin üzerine yazamaz (callback birleştirmesinde forma
/// öncelik verilir). Yazabilseydi başka bir ödemenin sonucu okutulabilirdi.
///
/// <b>İptal:</b> Craftgate'te ayrı bir iptal ucu yoktur — <c>/payment/v1/refunds</c>
/// gün içi ve kısmi iadesi olmayan işlemde CANCEL, aksi hâlde REFUND üretir. Kararı
/// sağlayıcı verdiği için hangisi olduğunu ham kod alanında taşıyoruz.
///
/// <b>⚠ SERTİFİKASYON DURUMU / TODO(cert):</b> imza dizisi, uçlar ve alan adları
/// sağlayıcının genel API belgelerine ve açık istemcilerine göre yazıldı; hata grubu
/// listesi eksik. Canlı hesapla doğrulanmadan üretime alınmamalı.
/// </summary>
public sealed class CraftgateConnector(IHttpClientFactory httpClientFactory) : IPaymentConnector
{
    public const string ConnectorKey = "craftgate";
    public const string HttpClientName = "poyra-craftgate";

    private const string OrtakSayfaBaslatYolu = "/payment/v1/checkout-payments/init";
    private const string UcluBaslatYolu = "/payment/v1/card-payments/3ds-init";
    private const string UcluTamamlaYolu = "/payment/v1/card-payments/3ds-complete";
    private const string IadeYolu = "/payment/v1/refunds";
    private const string KalemIadeYolu = "/payment/v1/refund-transactions";

    // Tarayıcının POST'ladığı "token"/"paymentId" bunların üzerine yazamasın diye önekli.
    private const string DurumToken = "poyra_cg_token";
    private const string DurumOdemeNo = "poyra_cg_payment_id";

    public string Key => ConnectorKey;

    public ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Craftgate — SERTİFİKASYON BEKLİYOR",
        ConnectorType.PaymentInstitution,
        [
            new CredentialField("gateway_base", "API adresi (ör. https://api.craftgate.io)"),
            new CredentialField("api_key", "API anahtarı (apiKey)"),
            new CredentialField("secret_key", "Gizli anahtar (secretKey)", Secret: true),
        ],
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Ortak Ödeme Sayfası (PCI kapsamı dışı) ve 3DS'li direct (PCI kapsamı içi) "
               + "birlikte desteklenir. Sonuç her iki akışta da SUNUCUDAN sorgulanır; sorgu "
               + "kimliği konnektör durumunda saklanır, tarayıcıdan alınmaz. İptal ayrı uç "
               + "değildir: iade ucu gün içi işlemde CANCEL üretir. İmza SERVİS ADRESİNİ de "
               + "kapsar — sandbox imzası canlıda geçmez. TODO(cert).");


    public async Task<HostedPaymentForm> InitiateHostedPaymentAsync(
        HostedPaymentRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        var tutar = CraftgateMessages.Price(request.AmountMinor);
        var taksit = Math.Max(1, request.Installments);

        using var yanit = await GonderAsync(credentials, HttpMethod.Post, OrtakSayfaBaslatYolu, new
        {
            price = tutar,
            paidPrice = tutar,
            currency = request.Currency.ToUpperInvariant(),
            paymentGroup = "PRODUCT",
            paymentPhase = "AUTH",
            paymentChannel = "WEB",
            conversationId = request.OrderId,
            externalId = request.OrderId,
            callbackUrl = request.CallbackUrl,
            clientIp = request.CustomerIp,
            // Taksit sayısı yukarıda çoktan karara bağlandı; sayfada tek seçenek açılır ki
            // müşteri başka bir taksite geçip tahsilat tutarını değiştiremesin.
            enabledInstallments = new[] { taksit },
            items = new[]
            {
                new { name = request.Description ?? "Siparis", price = tutar, externalId = request.OrderId },
            },
        }, ct);

        var kok = yanit.RootElement;
        var sayfa = Metin(kok, "pageUrl")
            ?? throw new ConnectorUnavailableException("Craftgate ortak ödeme sayfası adresi dönmedi.");
        var token = Metin(kok, "token")
            ?? throw new ConnectorUnavailableException("Craftgate ortak ödeme sayfası token dönmedi.");

        return new HostedPaymentForm(
            sayfa, new Dictionary<string, string>(), Method: "GET",
            ConnectorState: new Dictionary<string, string> { [DurumToken] = token });
    }


    public async Task<HostedPaymentForm?> InitiateThreeDsDirectAsync(
        DirectPaymentRequest request, string callbackUrl, ConnectorCredentials credentials,
        CancellationToken ct)
    {
        var tutar = CraftgateMessages.Price(request.AmountMinor);

        using var yanit = await GonderAsync(credentials, HttpMethod.Post, UcluBaslatYolu, new
        {
            price = tutar,
            paidPrice = tutar,
            currency = request.Currency.ToUpperInvariant(),
            installment = Math.Max(1, request.Installments),
            paymentGroup = "PRODUCT",
            paymentPhase = "AUTH",
            paymentChannel = "WEB",
            conversationId = request.OrderId,
            externalId = request.OrderId,
            clientIp = request.CustomerIp,
            callbackUrl,
            card = new
            {
                cardHolderName = request.Card.HolderName ?? "POYRA MUSTERI",
                cardNumber = request.Card.Pan,
                expireYear = request.Card.ExpiryYear.ToString("D4", CultureInfo.InvariantCulture),
                expireMonth = request.Card.ExpiryMonth.ToString("D2", CultureInfo.InvariantCulture),
                cvc = request.Card.Cvv,
                storeCardAfterSuccessPayment = false,
            },
            items = new[]
            {
                new { name = request.Description ?? "Siparis", price = tutar, externalId = request.OrderId },
            },
        }, ct);

        var kok = yanit.RootElement;

        var form = CraftgateMessages.FormuCoz(Metin(kok, "htmlContent"));
        if (form is not { } cikan)
            throw new ConnectorUnavailableException("Craftgate 3D yanıtında beklenen form yok.");

        // Tamamlama çağrısı bu kimlikle yapılır. Dönüşte tarayıcı da bir paymentId
        // POST'lar ama ona bakmıyoruz — başkasının ödemesini tamamlatabilirdi.
        var odemeNo = Metin(kok, "paymentId")
            ?? throw new ConnectorUnavailableException("Craftgate 3D başlatma paymentId dönmedi.");

        return new HostedPaymentForm(
            cikan.ActionUrl, cikan.Fields,
            ConnectorState: new Dictionary<string, string> { [DurumOdemeNo] = odemeNo });
    }

    /// <summary>
    /// Tarayıcı dönüşü TEK BAŞINA tahsilat kanıtı değildir — her iki akışta da sonuç
    /// <see cref="CompleteHostedCallbackAsync"/> içindeki sunucu çağrısından okunur.
    /// Burası bu yüzden asla başarı döndürmez.
    /// </summary>
    public HostedCallbackResult ParseAndValidateCallback(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials)
        => new(false, form.GetValueOrDefault("conversationId", string.Empty),
            null, null, null, null,
            UnifiedErrors.ProcessingError, null,
            "Craftgate dönüşü sunucu sorgusuyla kesinleştirilmelidir (CompleteHostedCallbackAsync).");

    public async Task<HostedCallbackResult> CompleteHostedCallbackAsync(
        IReadOnlyDictionary<string, string> form, ConnectorCredentials credentials, CancellationToken ct)
    {
        var token = form.GetValueOrDefault(DurumToken);
        var odemeNo = form.GetValueOrDefault(DurumOdemeNo);

        // Ortak sayfa akışı token ile, direct akış paymentId ile sorulur. İkisi de yoksa
        // sonuç bilinemez: tarayıcının gönderdiğine dönmek yerine başarısız saymak,
        // yanlış bir "ödendi"den her hâlükârda ucuzdur.
        using var yanit =
            !string.IsNullOrEmpty(token)
                ? await GonderAsync(credentials, HttpMethod.Get,
                    $"/payment/v1/checkout-payments/{Uri.EscapeDataString(token)}", null, ct)
            : long.TryParse(odemeNo, CultureInfo.InvariantCulture, out var no)
                ? await GonderAsync(credentials, HttpMethod.Post, UcluTamamlaYolu,
                    new { paymentId = no }, ct)
                : null;

        return yanit is null
            ? Basarisiz(form, "Craftgate sorgu kimliği dönüşte yok; sonuç doğrulanamadı.")
            : SonucuOku(yanit.RootElement, form);
    }

    private static HostedCallbackResult SonucuOku(JsonElement kok, IReadOnlyDictionary<string, string> form)
    {
        var durum = Metin(kok, "paymentStatus");
        var orderId = Metin(kok, "conversationId")
                      ?? form.GetValueOrDefault("conversationId", string.Empty);

        if (!CraftgateMessages.Onaylandi(durum))
        {
            var (grup, kod, mesaj) = HatayiOku(kok);
            return new HostedCallbackResult(
                false, orderId, null, null, null, null,
                CraftgateMessages.UnifiedError(grup, kod), kod ?? durum, mesaj);
        }

        return new HostedCallbackResult(
            true, orderId,
            AuthCode: Metin(kok, "authCode"),
            // İptal/iade paymentId ile yapılır — referansı burada saklıyoruz.
            ConnectorTxnId: Metin(kok, "id"),
            MaskedPan: Metin(kok, "binNumber") is { } bin ? bin + "******" : null,
            CardBank: Metin(kok, "cardIssuerBankName"),
            UnifiedErrors.None, durum, null);
    }


    public async Task<ConnectorOperationResult> VoidAsync(
        ConnectorReference reference, ConnectorCredentials credentials, CancellationToken ct)
    {
        if (!long.TryParse(reference.ConnectorTxnId, CultureInfo.InvariantCulture, out var odemeNo))
            return ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError, null, "Craftgate ödeme numarası yok; iptal yapılamaz.");

        using var yanit = await GonderAsync(credentials, HttpMethod.Post, IadeYolu, new
        {
            paymentId = odemeNo,
            conversationId = reference.OrderId,
            refundDestinationType = "PROVIDER",
        }, ct);

        var kok = yanit.RootElement;
        if (!CraftgateMessages.IadeOnaylandi(Metin(kok, "status")))
            return IadeHatasi(kok);

        // refundType = CANCEL | REFUND — hangisi olduğu ham kodda kalır.
        return new ConnectorOperationResult(
            true, reference.ConnectorTxnId, null, Metin(kok, "refundType"), null);
    }

    public async Task<ConnectorOperationResult> RefundAsync(
        ConnectorRefundRequest request, ConnectorCredentials credentials, CancellationToken ct)
    {
        if (!long.TryParse(request.ConnectorTxnId, CultureInfo.InvariantCulture, out var odemeNo))
            return ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError, null, "Craftgate ödeme numarası yok; iade yapılamaz.");

        var kalemNo = await KalemNumarasiAsync(credentials, odemeNo, ct);
        if (kalemNo is null)
            return ConnectorOperationResult.Fail(
                UnifiedErrors.ProcessingError, null,
                "Craftgate ödeme kalemi okunamadı; iade tutarı bir kaleme bağlanamadı.");

        using var yanit = await GonderAsync(credentials, HttpMethod.Post, KalemIadeYolu, new
        {
            paymentTransactionId = kalemNo.Value,
            conversationId = request.OrderId,
            refundPrice = CraftgateMessages.Price(request.AmountMinor),
            refundDestinationType = "PROVIDER",
        }, ct);

        var kok = yanit.RootElement;
        return CraftgateMessages.IadeOnaylandi(Metin(kok, "status"))
            ? ConnectorOperationResult.Ok(request.ConnectorTxnId)
            : IadeHatasi(kok);
    }

    private async Task<long?> KalemNumarasiAsync(
        ConnectorCredentials credentials, long odemeNo, CancellationToken ct)
    {
        using var yanit = await GonderAsync(credentials, HttpMethod.Get,
            $"/payment/v1/card-payments/{odemeNo.ToString(CultureInfo.InvariantCulture)}", null, ct);

        if (!yanit.RootElement.TryGetProperty("paymentTransactions", out var kalemler)
            || kalemler.ValueKind != JsonValueKind.Array || kalemler.GetArrayLength() == 0)
            return null;

        // Poyra tek kalemli sepet gönderiyor; birden fazlası gelirse iade tutarının
        // hangi kaleme yazılacağı belirsizdir ve sessizce ilkine yazmak yanlış olur.
        if (kalemler.GetArrayLength() > 1) return null;

        return long.TryParse(Metin(kalemler[0], "id"), CultureInfo.InvariantCulture, out var kalemNo)
            ? kalemNo
            : null;
    }


    private static ConnectorOperationResult IadeHatasi(JsonElement kok)
    {
        var (grup, kod, mesaj) = HatayiOku(kok);
        return ConnectorOperationResult.Fail(
            CraftgateMessages.UnifiedError(grup, kod),
            kod ?? Metin(kok, "status"),
            mesaj ?? "Craftgate iade/iptal onaylanmadı.");
    }

    private static HostedCallbackResult Basarisiz(
        IReadOnlyDictionary<string, string> form, string mesaj)
        => new(false, form.GetValueOrDefault("conversationId", string.Empty),
            null, null, null, null, UnifiedErrors.ProcessingError, null, mesaj);

    private static (string? Grup, string? Kod, string? Mesaj) HatayiOku(JsonElement kok)
    {
        var kaynak = kok.ValueKind == JsonValueKind.Object
                     && kok.TryGetProperty("paymentError", out var hata)
                     && hata.ValueKind == JsonValueKind.Object
            ? hata
            : kok;

        return (Metin(kaynak, "errorGroup"), Metin(kaynak, "errorCode"), Metin(kaynak, "errorDescription"));
    }

    private async Task<JsonDocument> GonderAsync(
        ConnectorCredentials credentials, HttpMethod yontem, string yol, object? govde, CancellationToken ct)
    {
        var adres = credentials.Require("gateway_base").TrimEnd('/');
        var json = govde is null ? string.Empty : JsonSerializer.Serialize(govde);
        var rastgele = CraftgateMessages.RastgeleAnahtar();

        using var istek = new HttpRequestMessage(yontem, adres + yol);
        if (govde is not null)
            istek.Content = new StringContent(json, Encoding.UTF8, "application/json");

        // İmza yolu ve SERVİS ADRESİNİ de kapsar: aynı gövdeyi başka bir uca ya da
        // başka bir ortama göndermek doğrulamayı kırar.
        istek.Headers.TryAddWithoutValidation("x-api-key", credentials.Require("api_key"));
        istek.Headers.TryAddWithoutValidation("x-rnd-key", rastgele);
        istek.Headers.TryAddWithoutValidation("x-auth-version", "v1");
        istek.Headers.TryAddWithoutValidation("x-signature", CraftgateMessages.Imza(
            adres, yol, credentials.Require("api_key"), credentials.Require("secret_key"),
            rastgele, json));
        istek.Headers.TryAddWithoutValidation("accept", "application/json");

        try
        {
            var istemci = httpClientFactory.CreateClient(HttpClientName);
            using var yanit = await istemci.SendAsync(istek, ct);
            var metin = await yanit.Content.ReadAsStringAsync(ct);

            // 4xx gövdesi hata ayrıntısını taşır ve çağıran onu okuyup birleşik koda
            // çevirebilmeli; yalnız 5xx/ağ hatası "konnektör ayakta değil" sayılır.
            if ((int)yanit.StatusCode >= 500)
                throw new ConnectorUnavailableException($"Craftgate {yol} → {(int)yanit.StatusCode}.");

            return JsonDocument.Parse(metin);
        }
        catch (HttpRequestException ex)
        {
            // Ham HttpRequestException sızarsa rota katmanı bunu failover'a uygun saymaz
            throw new ConnectorUnavailableException($"Craftgate {yol} ucuna ulaşılamadı.", ex);
        }
        catch (JsonException ex)
        {
            throw new ConnectorUnavailableException("Craftgate yanıtı JSON değil.", ex);
        }
    }

    private static string? Metin(JsonElement element, string ad)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(ad, out var deger)
            ? deger.ValueKind switch
            {
                JsonValueKind.String => deger.GetString(),
                JsonValueKind.Number => deger.ToString(),
                _ => null,
            }
            : null;
}
