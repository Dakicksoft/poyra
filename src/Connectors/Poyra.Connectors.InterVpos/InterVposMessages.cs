using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.InterVpos;

public static class InterVposMessages
{
    public const string TryCurrencyCode = "949";


    public static string Amount(long amountMinor)
        => (amountMinor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// 3D Pay isteğinin hash'i.
    ///
    /// <b>TODO(cert):</b> alan SIRASI bankanın dokümanıyla doğrulanmalı. Sıra yanlışsa
    /// banka isteği "hash uyuşmadı" diye reddeder ve neden reddettiğini söylemez.
    /// Bilinen desen: ShopCode + OrderId + PurchAmount + OkUrl + FailUrl + TxnType
    /// + InstallmentCount + Rnd + merchantPass
    /// </summary>
    public static string RequestHash(
        string shopCode, string orderId, string amount, string okUrl, string failUrl,
        string txnType, string installment, string random, string merchantPass)
        => Sha1Base64(string.Concat(
            shopCode, orderId, amount, okUrl, failUrl, txnType, installment, random, merchantPass));

    /// <summary>
    /// Banka dönüşünün hash'i — <b>dönüşe asla körü körüne güvenilmez</b>.
    ///
    /// Doğrulanmayan bir callback, tarayıcıdan gelen sahte bir "onaylandı" POST'unun
    /// tahsilat sayılması demektir.
    ///
    /// <b>TODO(cert):</b> InterVPOS dönüşte hangi alanları hash'lediğini dokümanla
    /// bildirir; aşağıdaki sıra doğrulanmadan güvenilmemelidir.
    /// </summary>
    public static string CallbackHash(
        string shopCode, string orderId, string procReturnCode, string response,
        string random, string merchantPass)
        => Sha1Base64(string.Concat(shopCode, orderId, procReturnCode, response, random, merchantPass));

    /// <summary>
    /// Taksit alanı: tek çekim <b>boş string</b> gönderilir, "1" değil.
    ///
    /// Bu TR sanal POS'larında yaygın bir tuzaktır: "1" yazmak bazı bankalarda
    /// "1 taksit" kampanyası sayılır ve işlem farklı komisyonla geçer.
    /// <b>TODO(cert):</b> Denizbank'ın bu alandaki beklentisi teyit edilmeli.
    /// </summary>
    public static string Installment(int count) => count <= 1 ? string.Empty : count.ToString();

    /// <summary>Banka dönüşü başarılı mı — <c>Response</c> alanı "Approved" olmalı.</summary>
    public static bool IsApproved(IReadOnlyDictionary<string, string> form)
        => form.TryGetValue("Response", out var response)
           && string.Equals(response, "Approved", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Banka hata kodunu Poyra'nın birleşik sözlüğüne çevirir.
    ///
    /// <b>TODO(cert):</b> ProcReturnCode listesi bankadan alınmalı. Eşlenmeyen kod
    /// <c>poyra.card_declined</c> sayılır — yanlış eşlemek, yeniden denenebilir bir
    /// hatayı terminal göstermek (ya da tersi) demektir.
    /// </summary>
    public static string UnifiedError(string? procReturnCode) => procReturnCode switch
    {
        "51" => UnifiedErrors.InsufficientFunds,
        "54" => UnifiedErrors.ExpiredCard,
        "14" or "15" => UnifiedErrors.InvalidCard,
        "57" or "62" => UnifiedErrors.NotPermitted,
        "61" or "65" => UnifiedErrors.LimitExceeded,
        null or "" => UnifiedErrors.ProcessingError,
        _ => UnifiedErrors.CardDeclined,
    };

    private static string Sha1Base64(string value)
        => Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Sunucu yanıtı <c>Ad=Deger</c> çiftlerinin <c>;;</c> ya da <c>;;;</c> ile
    /// ayrılmasından oluşur (JSON/XML değil). Bozuk gövde boş sözlük döner —
    /// çağıran "başarısız" sayar (fail closed).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Oku(string govde)
    {
        var sonuc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(govde)) return sonuc;

        foreach (var parca in govde.Split([";;;", ";;"], StringSplitOptions.RemoveEmptyEntries))
        {
            var esittir = parca.IndexOf('=');
            if (esittir <= 0) continue;

            sonuc.TryAdd(parca[..esittir].Trim(), parca[(esittir + 1)..].Trim());
        }

        return sonuc;
    }
}
