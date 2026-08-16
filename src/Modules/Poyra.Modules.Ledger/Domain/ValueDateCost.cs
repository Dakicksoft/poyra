using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Ledger.Domain;

/// <summary>
/// İşyerinin defter ayarları. Bugün tek alan taşıyor ama ayrı bir varlık olarak
/// duruyor: finansman oranı işyerine özgüdür ve bir yere iliştirilirse kaybolur.
/// </summary>
public sealed class LedgerSettings : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }

    /// <summary>
    /// İşyerinin <b>yıllık finansman maliyeti</b>, baz puan (3500 = %35).
    ///
    /// <b>Neden ayar, neden varsayılan yok:</b> bu oran işyerinin kendi kredi
    /// maliyetidir — kimi işyeri nakitle çalışır, kimi rotatif krediyle. Merkezî
    /// bir oranı (politika faizi vb.) koda gömmek iki hata yapardı: yanlış rakam
    /// verirdi ve rakam değiştiğinde kimse fark etmezdi.
    ///
    /// Tanımlanmadıysa valör maliyeti HESAPLANMAZ; uydurulmuş bir oran, olmayan
    /// bir zararı varmış gibi gösterir.
    /// </summary>
    public int? AnnualFinancingRateBps { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Valör gecikmesinin <b>parasal</b> karşılığı.
///
/// <b>Neden gerekli:</b> "banka 3 gün geç ödedi" bir şikâyettir; "banka 3 gün geç
/// ödediği için bu ay 18.400 ₺ finansman maliyetine girdiniz" bir karardır. TR faiz
/// ortamında gecikme gerçek paradır ve CFO'nun anladığı tek dil budur.
/// </summary>
public static class ValueDateCost
{
    /// <summary>
    /// Gün sayacı: TRY'de bankacılık pratiği <b>365</b> gündür (ACT/365).
    /// 360 kullanmak maliyeti %1,4 abartırdı — bankayla konuşurken tartışma
    /// rakamın kendisinde değil, yönteminde çıkardı.
    /// </summary>
    public const int DaysInYear = 365;

    /// <summary>
    /// <c>tutar × (yıllık oran / 365) × gecikme günü</c>
    ///
    /// Gecikme sıfır ya da negatifse (erken ödeme) maliyet YOKTUR — sıfır döner.
    /// Erken ödeme bir kazançtır ama <b>gecikmeyle netleştirilmez</b>: aksi hâlde
    /// ara sıra erken ödeyen bir banka, kronik gecikmesini gizleyebilirdi.
    /// </summary>
    public static long Of(long amountMinor, int delayDays, int annualRateBps)
    {
        if (delayDays <= 0 || amountMinor <= 0 || annualRateBps <= 0)
            return 0;

        var cost = (decimal)amountMinor * annualRateBps / 10_000m / DaysInYear * delayDays;

        // Bankacı yuvarlaması: tek yönde yuvarlamak çok sayıda kayıtta
        // sistematik sapma üretir
        return (long)Math.Round(cost, MidpointRounding.ToEven);
    }
}
