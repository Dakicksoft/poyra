namespace Poyra.Modules.Subscriptions.Domain;

public enum DunningAction
{
    Retry, // NextAttemptAt'te yeniden dene
    RequestCardUpdate, // kör deneme yapma — müşteriden yeni kart iste
    Abandon, // terminal: kurtarma şansı yok
}

public sealed record DunningDecision(DunningAction Action, DateTimeOffset? NextAttemptAt, string Reason);

/// <summary>
/// Akıllı dunning.
/// Kör "3 gün sonra tekrar dene" yerine hata koduna göre strateji:
///
///  • insufficient_funds → TR maaş günü hipotezi: bir sonraki ayın 1'i veya 15'i
///    (hangisi önce geliyorsa; aynı güne denk gelirse sıradaki pencere).
///    TR'de maaşlar büyük ölçüde ayın 1'i ve 15'inde yatar — limit yetersizliğinin
///    en yüksek kurtarma olasılığı bu pencerelerdir.
///  • expired_card → deneme YAPMA: kart güncelleme iste (kör deneme hem başarısız
///    olur hem de bankada "sık ret" sinyali üretir).
///  • do_not_honor / issuer_unavailable / connector_unavailable → geçici olabilir:
///    üstel geri çekilme (1g → 3g → 7g).
///  • invalid_card / not_permitted / limit_exceeded → terminal: bırak.
///
/// Toplam deneme hakkı MaxAttempts (ilk tahsilat dahil).
/// </summary>
public static class DunningPolicy
{
    public const int MaxAttempts = 4;

    private static readonly TimeSpan[] BackoffDelays =
    [
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(3),
        TimeSpan.FromDays(7),
    ];

    public static DunningDecision Decide(string? unifiedCode, int attemptCount, DateTimeOffset now)
    {
        if (attemptCount >= MaxAttempts)
            return new DunningDecision(DunningAction.Abandon, null,
                $"Deneme hakkı doldu ({attemptCount}/{MaxAttempts}).");

        return unifiedCode switch
        {
            "poyra.insufficient_funds" => new DunningDecision(
                DunningAction.Retry, NextSalaryWindow(now),
                "Limit yetersiz — bir sonraki maaş penceresinde (ayın 1'i/15'i) denenecek."),

            "poyra.expired_card" => new DunningDecision(
                DunningAction.RequestCardUpdate, null,
                "Kartın süresi dolmuş — müşteriden güncel kart isteniyor (kör deneme yapılmaz)."),

            "poyra.card_declined" or "poyra.issuer_unavailable" or "poyra.connector_unavailable"
                or "poyra.processing_error" => new DunningDecision(
                    DunningAction.Retry, now + BackoffDelays[Math.Min(attemptCount - 1, BackoffDelays.Length - 1)],
                    "Geçici ret olabilir — üstel geri çekilmeyle yeniden denenecek."),

            "poyra.invalid_card" or "poyra.transaction_not_permitted" or "poyra.limit_exceeded"
                => new DunningDecision(DunningAction.Abandon, null,
                    "Kalıcı ret — yeniden deneme kurtarma sağlamaz."),

            _ => new DunningDecision(
                DunningAction.Retry, now + BackoffDelays[Math.Min(attemptCount - 1, BackoffDelays.Length - 1)],
                "Bilinmeyen hata — temkinli geri çekilmeyle denenecek."),
        };
    }

    /// <summary>now'dan SONRAKİ ilk maaş penceresi (ayın 1'i veya 15'i), UTC 09:00.</summary>
    public static DateTimeOffset NextSalaryWindow(DateTimeOffset now)
    {
        var candidates = new[]
        {
            new DateTimeOffset(now.Year, now.Month, 15, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(now.Year, now.Month, 1, 9, 0, 0, TimeSpan.Zero).AddMonths(1),
            new DateTimeOffset(now.Year, now.Month, 15, 9, 0, 0, TimeSpan.Zero).AddMonths(1),
        };

        return candidates.First(c => c > now);
    }
}
