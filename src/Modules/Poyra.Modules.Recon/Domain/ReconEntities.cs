using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Recon.Domain;


public sealed class CommissionAgreement : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid ConnectorAccountId { get; init; }
    public int InstallmentCount { get; set; } // 1 = tek çekim
    public int RateBps { get; set; } // 250 = %2,50
    public int ValorDays { get; set; } // hesaba geçiş gün sayısı
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum StatementStatus
{
    Imported,
    Matching, // büyük ekstre — eşleştirme Hangfire'da
    Matched,
}

/// <summary>Bir banka/PSP gün sonu ekstresi. Silinmez — kanıt belgesidir.</summary>
public sealed class ReconStatement : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid ConnectorAccountId { get; init; }
    public DateOnly StatementDate { get; init; }
    public StatementStatus Status { get; set; } = StatementStatus.Imported;
    public int LineCount { get; set; }
    public int RefundLineCount { get; set; }
    public int MatchedCount { get; set; }
    public int MissingInPoyraCount { get; set; }
    public int AmountMismatchCount { get; set; }
    public int MissingInStatementCount { get; set; }

    /// <summary>Eşleşen iade satırlarında bankanın kestiği komisyon toplamı — çoğu ankaşmada 0 beklenir; &gt;0 panelde dikkat çeker (politika-bazlı denetim F3).</summary>
    public long RefundCommissionSum { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum LineMatchStatus
{
    Pending, // eşleştirme (Hangfire)
    Matched,
    MissingInPoyra, // ekstrede var, defterimizde yok
    AmountMismatch, // sipariş eşleşti ama brüt tutar farklı
}

public enum StatementLineType
{
    Sale,
    Refund, // sipariş no orijinal satışa (att_…) işaret eder; iadeyle tutar üzerinden eşlenir
}

public sealed class ReconStatementLine : ITenantOwned, IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid StatementId { get; init; }
    public int LineNo { get; init; }
    public required string OrderId { get; init; } // att_… beklenir
    public StatementLineType LineType { get; init; } = StatementLineType.Sale;
    public long GrossMinor { get; init; }
    public long CommissionMinor { get; init; } // bankanın kestiği
    public long NetMinor { get; init; }
    public DateOnly? ValueDate { get; init; } // valör (bankanın beyan ettiği hesaba geçiş)
    public LineMatchStatus MatchStatus { get; set; } = LineMatchStatus.Pending;
    public Guid? MatchedAttemptId { get; set; }
    public Guid? MatchedRefundId { get; set; }

    /// <summary>Anlaşma valöründen beklenen hesaba geçiş günü (satış satırı + anlaşma varsa).</summary>
    public DateOnly? ExpectedValueDate { get; set; }

    /// <summary>value_date - expected (gün): &gt;0 banka GEÇ ödemiş.</summary>
    public int? ValorDeltaDays { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}


public sealed class CommissionAuditFinding : ITenantOwned, IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid StatementId { get; init; }
    public Guid StatementLineId { get; init; }
    public Guid AttemptId { get; init; }
    public int Installments { get; init; }
    public long GrossMinor { get; init; }
    public long ActualCommissionMinor { get; init; }

    /// <summary>Anlaşma yoksa null — "denetlenemedi" ayrı sayılır, sıfır farkla karışmaz.</summary>
    public long? ExpectedCommissionMinor { get; init; }

    public int? AgreedRateBps { get; init; }
    public long? DeltaMinor { get; init; } // actual - expected
    public DateTimeOffset CreatedAt { get; set; }
}
