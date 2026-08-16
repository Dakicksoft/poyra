using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Ledger.Domain;

/// <summary>
/// İşyerinin BANKA HESAP ekstresi — zincirin üçüncü halkası.
///
/// <b>POS ekstresinden farkı:</b> POS ekstresi bankanın "ödeyeceğim" dediği yerdir;
/// hesap ekstresi "ödedim" dediği yer. İkisinin tutmadığı durumlar Türkiye'de sık
/// (eksik ödeme, geç valör, netleştirilmiş kesinti, iade mahsubu) ve bugün kimse
/// kontrol etmiyor — çünkü iki ayrı sistemde duruyorlar.
/// </summary>
public sealed class SettlementStatement : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public string PublicId { get; private set; } = null!; // set_…

    /// <summary>
    /// Hangi POS hesabının hasılatını taşıdığı.
    /// </summary>
    public Guid ConnectorAccountId { get; init; }

    public required string Format { get; init; } // mt940 · poyra_csv
    public required string FileName { get; init; }
    public int LineCount { get; set; }

    /// <summary>Ekstrenin kapsadığı ilk/son valör günü — rapor penceresi.</summary>
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Hesap ekstresindeki tek hareket.
///
/// Tutar İŞARETLİDİR: alacak (para girdi) pozitif, borç (para çıktı) negatif.
/// </summary>
public sealed class SettlementLine : ITenantOwned, IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid StatementId { get; init; }
    public int LineNo { get; init; }

    /// <summary>Valör günü — paranın hesapta kullanılabilir olduğu gün.</summary>
    public DateOnly ValueDate { get; init; }

    /// <summary>Kuruş, işaretli. Alacak (+) / borç (−).</summary>
    public long AmountMinor { get; init; }

    public string? Description { get; init; }
    public string? Reference { get; init; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Gün bazında hesaba geçiş sonucu.</summary>
public enum SettlementOutcome
{
    /// <summary>Beklenen tutar kuruşu kuruşuna yattı.</summary>
    Settled = 10,

    /// <summary>Yattı ama EKSİK. Aradaki fark bankadan sorulacak paradır.</summary>
    Short = 20,

    /// <summary>Beklenenden FAZLA yattı — muhtemelen başka bir günün hasılatı karışmış.</summary>
    Over = 30,

    /// <summary>O gün hiç para yatmamış. En ağır bulgu.</summary>
    Missing = 40,
}

public static class SettlementOutcomeMap
{
    public static readonly IReadOnlyDictionary<SettlementOutcome, string> ToDb =
        new Dictionary<SettlementOutcome, string>
        {
            [SettlementOutcome.Settled] = "settled",
            [SettlementOutcome.Short] = "short",
            [SettlementOutcome.Over] = "over",
            [SettlementOutcome.Missing] = "missing",
        };

    public static readonly IReadOnlyDictionary<string, SettlementOutcome> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

/// <summary>
/// <b>Üç yönlü mutabakatın bulgusu:</b> "söz verilen para gerçekten geldi mi?"
///
/// Bir POS hesabının bir valör günü için: alacaklardan beklenen toplam ile hesap
/// ekstresine gerçekten yatan toplamı karşılaştırır.
///
/// <b>Değiştirilemez</b> : bulgu, bankayla yapılacak görüşmenin dayanağıdır.
/// Sonradan düzeltilebilen bir kanıt, kanıt değildir. Yeni bilgi geldiğinde eski
/// bulgu silinmez — yeni bulgu yazılır.
/// </summary>
public sealed class SettlementFinding : ITenantOwned, IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid ConnectorAccountId { get; init; }
    public Guid StatementId { get; init; }

    public DateOnly ValueDate { get; init; }

    /// <summary>Alacaklardan beklenen net toplam (satış − iade).</summary>
    public long ExpectedMinor { get; init; }

    /// <summary>Hesap ekstresine gerçekten yatan toplam.</summary>
    public long ActualMinor { get; init; }

    /// <summary>
    /// <c>Actual − Expected</c>. <b>Negatif = eksik yattı</b> — bankadan sorulacak para.
    /// </summary>
    public long DeltaMinor { get; init; }

    public int ReceivableCount { get; init; }
    public SettlementOutcome Outcome { get; init; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Tutar karşılaştırması <b>tam</b> yapılır, tolerans YOKTUR.
    ///
    /// "Birkaç kuruş fark önemsizdir" demek, ürünün varlık sebebini inkâr etmektir:
    /// sistematik bir kuruş farkı milyonlarca işlemde gerçek paradır ve tolerans
    /// tam olarak onu görünmez kılar.
    /// </summary>
    public static SettlementOutcome Classify(long expectedMinor, long actualMinor)
        => actualMinor == 0 && expectedMinor != 0 ? SettlementOutcome.Missing
            : actualMinor == expectedMinor ? SettlementOutcome.Settled
            : actualMinor < expectedMinor ? SettlementOutcome.Short
            : SettlementOutcome.Over;
}
