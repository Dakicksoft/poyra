using System.Globalization;
using Poyra.Modules.Payments.Domain;

namespace Poyra.Panel.Components;


public static class Fmt
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Money(long amountMinor, string currency)
    {
        var symbol = currency switch { "TRY" => "₺", "USD" => "$", "EUR" => "€", _ => currency };
        return $"{(amountMinor / 100m).ToString("N2", Tr)} {symbol}";
    }

    public static string Date(DateTimeOffset value)
        => value.ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm", Tr);

    public static string DateOnly(DateOnly value) => value.ToString("dd.MM.yyyy", Tr);

    public static string ShortId(string? id, int length = 14)
        => id is null ? "—" : id.Length <= length ? id : id[..length] + "…";

    public static string Percent(double ratio) => string.Create(Tr, $"%{ratio * 100:0.0}");

    public static string RateBps(int bps) => string.Create(Tr, $"%{bps / 100m:0.00}");

    public static string PaymentStatus(PaymentStatus status) => status switch
    {
        Poyra.Modules.Payments.Domain.PaymentStatus.Created => "Oluşturuldu",
        Poyra.Modules.Payments.Domain.PaymentStatus.RequiresAction => "3D Bekliyor",
        Poyra.Modules.Payments.Domain.PaymentStatus.Processing => "İşleniyor",
        Poyra.Modules.Payments.Domain.PaymentStatus.Succeeded => "Başarılı",
        Poyra.Modules.Payments.Domain.PaymentStatus.Failed => "Başarısız",
        Poyra.Modules.Payments.Domain.PaymentStatus.Cancelled => "İptal",
        Poyra.Modules.Payments.Domain.PaymentStatus.Expired => "Süresi doldu",
        _ => status.ToString(),
    };

    public static string StatusClass(PaymentStatus status) => status switch
    {
        Poyra.Modules.Payments.Domain.PaymentStatus.Succeeded => "ok",
        Poyra.Modules.Payments.Domain.PaymentStatus.Failed => "err",
        Poyra.Modules.Payments.Domain.PaymentStatus.RequiresAction => "wait",
        Poyra.Modules.Payments.Domain.PaymentStatus.Cancelled => "muted",
        _ => "info",
    };

    public static string Error(string? unifiedCode) => unifiedCode switch
    {
        null or "" => "",
        "poyra.card_declined" => "Kart reddedildi",
        "poyra.insufficient_funds" => "Limit yetersiz",
        "poyra.expired_card" => "Kartın süresi dolmuş",
        "poyra.invalid_card" => "Geçersiz kart",
        "poyra.invalid_amount" => "Geçersiz tutar",
        "poyra.transaction_not_permitted" => "İşleme izin verilmedi",
        "poyra.limit_exceeded" => "Limit aşıldı",
        "poyra.three_ds_failed" => "3D doğrulama başarısız",
        "poyra.three_ds_timeout" => "3D doğrulama zaman aşımı",
        "poyra.callback_signature_invalid" => "Banka dönüş imzası geçersiz",
        "poyra.issuer_unavailable" => "Banka erişilemiyor",
        "poyra.connector_unavailable" => "POS erişilemiyor",
        "poyra.void_window_closed" => "Gün sonu kapandı (iade gerekir)",
        "poyra.processing_error" => "İşlem hatası",
        _ => unifiedCode,
    };

    public static string PaymentStatus(string status)
        => Enum.TryParse<PaymentStatus>(status, ignoreCase: true, out var parsed)
            ? PaymentStatus(parsed)
            : status switch
            {
                "requires_action" => "3D Bekliyor",
                _ => status,
            };

    public static string StatusClass(string status)
        => Enum.TryParse<PaymentStatus>(status, ignoreCase: true, out var parsed)
            ? StatusClass(parsed)
            : status == "requires_action" ? "wait" : "info";

    public static string StatementStatus(string status) => status switch
    {
        "imported" => "İçe aktarıldı",
        "matching" => "Eşleştiriliyor",
        "matched" => "Eşleşti",
        _ => status,
    };

    public static string StatementStatusClass(string status) => status switch
    {
        "matched" => "ok",
        "matching" => "wait",
        _ => "info",
    };

    public static string Health(string health) => health switch
    {
        "healthy" => "Sağlıklı",
        "degraded" => "Yavaş",
        "down" => "Erişilemiyor",
        _ => health,
    };

    public static string HealthClass(string health) => health switch
    {
        "healthy" => "ok",
        "degraded" => "warn",
        "down" => "err",
        _ => "muted",
    };

    public static string Interval(string unit, int count)
    {
        var (one, each) = unit switch
        {
            "day" => ("Her gün", "günde"),
            "week" => ("Her hafta", "haftada"),
            "month" => ("Her ay", "ayda"),
            "year" => ("Her yıl", "yılda"),
            _ => (unit, unit),
        };
        return count <= 1 ? one : $"{count} {each} bir";
    }

    public static string SubscriptionStatus(string status) => status switch
    {
        "trialing" => "Deneme",
        "active" => "Aktif",
        "past_due" => "Gecikmiş",
        "paused" => "Duraklatıldı",
        "cancelled" => "İptal",
        "unpaid" => "Ödenmedi",
        _ => status,
    };

    public static string InvoiceStatus(string status) => status switch
    {
        "pending" => "Bekliyor",
        "paid" => "Ödendi",
        "retrying" => "Yeniden denenecek",
        "abandoned" => "Vazgeçildi",
        _ => status,
    };
}
