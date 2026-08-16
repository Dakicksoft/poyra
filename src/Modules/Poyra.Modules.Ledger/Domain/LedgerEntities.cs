using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Ledger.Domain;

/// <summary>Alacağı doğuran işlemin türü.</summary>
public enum ReceivableKind
{
    /// <summary>Tahsilat — banka işyerine borçlanır (pozitif).</summary>
    Sale = 10,

    /// <summary>
    /// İade — alacak AZALIR (negatif). Ayrı bir tür olarak durur çünkü iadenin
    /// komisyonu satışınkiyle aynı olmayabilir ve raporda ayrı görünmelidir.
    /// </summary>
    Refund = 20,
}

public static class ReceivableKindMap
{
    public static readonly IReadOnlyDictionary<ReceivableKind, string> ToDb =
        new Dictionary<ReceivableKind, string>
        {
            [ReceivableKind.Sale] = "sale",
            [ReceivableKind.Refund] = "refund",
        };

    public static readonly IReadOnlyDictionary<string, ReceivableKind> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

/// <summary>
/// Alacağın yaşam döngüsü. Üç ayrı soruya cevap verir ve karıştırılmamalıdır:
/// banka <b>biliyor mu</b>, <b>ne kadar</b> diyor, <b>ödedi mi</b>.
/// </summary>
public enum ReceivableStatus
{
    /// <summary>
    /// Tahsilat oldu, banka henüz teyit etmedi. Beklenen tutar ANLAŞMADAN hesaplandı —
    /// yani bu bizim iddiamız, bankanın değil.
    /// </summary>
    Expected = 10,

    /// <summary>
    /// POS ekstresinde göründü: banka borcu kabul etti ve GERÇEK komisyonunu söyledi.
    /// Beklenenle farkı varsa komisyon denetimi (M14) bunu bulgu olarak yazar.
    /// </summary>
    BankConfirmed = 20,

    /// <summary>
    /// Para işyerinin banka hesabına GERÇEKTEN geçti.
    ///
    /// <b>Bu durumu bugün hiçbir şey yazmaz</b> — yazabilmek için hesap ekstresi
    /// (üç yönlü mutabakat) gerekir. Kasıtlı olarak burada duruyor: zincirin
    /// eksik halkası kodda görünsün, unutulmasın.
    /// </summary>
    Settled = 30,

    /// <summary>
    /// Ekstrede hiç görünmedi ya da beklenen valör geçtiği hâlde teyit gelmedi.
    /// Takip edilecek olan budur.
    /// </summary>
    Overdue = 40,

    /// <summary>Harcama itirazı/ters kayıt ile alacak düştü.</summary>
    Reversed = 50,
}

public static class ReceivableStatusMap
{
    public static readonly IReadOnlyDictionary<ReceivableStatus, string> ToDb =
        new Dictionary<ReceivableStatus, string>
        {
            [ReceivableStatus.Expected] = "expected",
            [ReceivableStatus.BankConfirmed] = "bank_confirmed",
            [ReceivableStatus.Settled] = "settled",
            [ReceivableStatus.Overdue] = "overdue",
            [ReceivableStatus.Reversed] = "reversed",
        };

    public static readonly IReadOnlyDictionary<string, ReceivableStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

public sealed class BankReceivable : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public string PublicId { get; private set; } = null!; // rcv_…

    public Guid ConnectorAccountId { get; init; }
    public ReceivableKind Kind { get; init; }

    /// <summary>
    /// Bankaya giden sipariş numarası (att_…). İşyeri içinde TEKİLDİR: alacağın
    /// iki kez yazılması, işyerine olmayan parayı borç göstermek olurdu. Projeksiyon
    /// işinin yeniden koşması bu yüzden zararsızdır.
    /// </summary>
    public required string AttemptPublicId { get; init; }

    /// <summary>İzlenebilirlik: hangi ödemeden doğdu (pay_…).</summary>
    public string? PaymentPublicId { get; init; }

    /// <summary>
    /// Karttan ÇEKİLEN tutar (vade farkı dahil). Mal bedeli değil — bankanın
    /// komisyonu bunun üzerinden hesaplanır.
    /// </summary>
    public long GrossMinor { get; init; }

    public string Currency { get; init; } = "TRY";
    public int Installments { get; init; }

    // --- BİZİM İDDİAMIZ (anlaşmadan hesaplanır) ---

    /// <summary>Anlaşma oranı yoksa null — o zaman beklenen tutar hesaplanamaz.</summary>
    public int? ExpectedRateBps { get; init; }

    public long? ExpectedCommissionMinor { get; init; }
    public long? ExpectedNetMinor { get; init; }

    /// <summary>
    /// Paranın hesaba geçmesi beklenen gün. Valör <b>iş günü</b> sayılır: hafta sonu
    /// ve resmî tatil banka ödemesi yapmaz, takvim günü saymak yanlış gecikme üretirdi.
    /// </summary>
    public DateOnly? ExpectedValueDate { get; init; }

    // --- BANKANIN SÖYLEDİĞİ (POS ekstresinden) ---

    public long? ConfirmedCommissionMinor { get; set; }
    public long? ConfirmedNetMinor { get; set; }
    public DateOnly? ConfirmedValueDate { get; set; }
    public DateTimeOffset? BankConfirmedAt { get; set; }

    /// <summary>Tahsilatın SUNUCU zamanı — valör buradan sayılır.</summary>
    public DateTimeOffset CapturedAtServer { get; init; }

    public ReceivableStatus Status { get; set; } = ReceivableStatus.Expected;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Beklenen ile teyit edilen komisyon farkı. Pozitif = banka FAZLA kesmiş.
    /// Teyit yoksa null — "fark yok" ile "henüz bilmiyoruz" karıştırılmamalı.
    /// </summary>
    public long? CommissionDeltaMinor
        => ExpectedCommissionMinor is { } expected && ConfirmedCommissionMinor is { } confirmed
            ? confirmed - expected
            : null;

    /// <summary>
    /// Valör gecikmesi (gün). Teyit varsa gerçek, yoksa bugüne göre tahmini.
    /// Negatif = erken ödenmiş.
    /// </summary>
    public int? ValueDateDelayDays(DateOnly today)
    {
        if (ExpectedValueDate is not { } expected)
            return null;

        var actual = ConfirmedValueDate ?? today;
        return actual.DayNumber - expected.DayNumber;
    }

    /// <summary>
    /// Alacak gecikti mi: beklenen valör geçmiş ama banka hâlâ teyit etmemiş.
    ///
    /// Teyit edilmiş bir alacak burada gecikmiş SAYILMAZ — bankanın kabul ettiği
    /// ama henüz ödemediği para ayrı bir sorundur ve onu üç yönlü mutabakat
    /// (hesap ekstresi) yakalar.
    /// </summary>
    public bool IsOverdue(DateOnly today)
        => Status == ReceivableStatus.Expected
           && ExpectedValueDate is { } due
           && due < today;

    /// <summary>
    /// Anlaşma oranından beklenen komisyon. <b>Bankacı yuvarlaması</b> (çift sayıya)
    /// kullanılır: tek yönde sistematik yuvarlama, çok sayıda işlemde işyerinin
    /// aleyhine birikir.
    /// </summary>
    public static long CommissionOf(long grossMinor, int rateBps)
        => (long)Math.Round(grossMinor * (decimal)rateBps / 10_000m, MidpointRounding.ToEven);
}
