using System.Globalization;
using Poyra.Modules.Disputes.Features;

namespace Poyra.Panel.Components;


public static class DisputeText
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Effective(DisputeResponse dispute)
        => dispute.Status == "open" && dispute.Overdue ? "expired" : dispute.Status;

    public static string Status(string status) => status switch
    {
        "open" => "Kanıt bekleniyor",
        "under_review" => "İncelemede",
        "accepted" => "Vazgeçildi",
        "expired" => "Süre doldu",
        "won" => "Kazanıldı",
        "lost" => "Kaybedildi",
        _ => status,
    };

    public static string StatusClass(string status) => status switch
    {
        "won" => "ok",
        "lost" or "expired" => "err",
        "under_review" => "info",
        "accepted" => "muted",
        _ => "wait",
    };

    public static string Stage(string stage) => stage switch
    {
        "retrieval" => "Bilgi talebi",
        "chargeback" => "Harcama itirazı",
        "pre_arbitration" => "Ön hakem",
        "arbitration" => "Hakem heyeti",
        _ => stage,
    };

    public static string Reason(string reason) => reason switch
    {
        "poyra.dispute.fraud" => "Dolandırıcılık iddiası",
        "poyra.dispute.product_not_received" => "Ürün teslim edilmedi",
        "poyra.dispute.product_not_as_described" => "Ürün tarife uymuyor",
        "poyra.dispute.duplicate" => "Çift çekim",
        "poyra.dispute.incorrect_amount" => "Yanlış tutar",
        "poyra.dispute.credit_not_processed" => "İade yapılmadı",
        "poyra.dispute.subscription_cancelled" => "İptal edilen abonelik",
        "poyra.dispute.unrecognized" => "Tanınmayan işlem",
        "poyra.dispute.other" => "Diğer",
        _ => reason,
    };

    public static string Kind(string kind) => kind switch
    {
        "delivery_proof" => "Teslim kanıtı",
        "invoice" => "Fatura",
        "contract" => "Sözleşme / şartlar",
        "communication" => "Müşteri yazışması",
        "refund_proof" => "İade kanıtı",
        "three_ds" => "3D doğrulama kaydı",
        "other" => "Diğer",
        _ => kind,
    };

    public static readonly string[] Kinds =
        ["delivery_proof", "invoice", "contract", "communication", "refund_proof", "three_ds", "other"];

    public static string Remaining(DisputeResponse dispute)
    {
        if (dispute.Status != "open")
            return "—";

        var hours = dispute.RemainingHours;
        if (hours <= 0)
            return "SÜRE DOLDU";

        if (hours < 24)
            return $"{Math.Floor(hours).ToString("N0", Tr)} saat";

        var days = (int)Math.Floor(hours / 24);

        // Kritik pencerede (72 saat altı) "1 gün" yuvarlaması 23 saati gizler:
        // 25 saat de 47 saat de "1 gün" görünürdü. Gün + saat birlikte yazılır.
        if (hours < 72)
        {
            var restHours = (int)Math.Floor(hours - days * 24);
            return restHours > 0 ? $"{days} gün {restHours} saat" : $"{days} gün";
        }

        return $"{days} gün";
    }


    public static string RemainingClass(DisputeResponse dispute) => dispute.Status switch
    {
        not "open" => "muted",
        _ when dispute.RemainingHours < 72 => "err",
        _ when dispute.RemainingHours < 168 => "warn",
        _ => "ok",
    };


    public static string Event(string eventType) => eventType switch
    {
        "dispute.opened" => "Dosya açıldı",
        "dispute.evidence_added" => "Belge yüklendi",
        "dispute.evidence_revoked" => "Belge iptal edildi",
        "dispute.evidence_submitted" => "Savunma iletildi",
        "dispute.evidence_due_soon" => "Süre uyarısı",
        "dispute.accepted" => "Savunmadan vazgeçildi",
        "dispute.won" => "Kazanıldı",
        "dispute.lost" => "Kaybedildi",
        "dispute.expired" => "Süre doldu",
        "dispute.escalated" => "Üst kademeye taşındı",
        _ => eventType,
    };
}
