using System.Text.Json;
using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Disputes.Domain;


public enum DisputeStage
{
    /// <summary>Bilgi/belge talebi — henüz para geri alınmadı, belge istenir.</summary>
    Retrieval = 10,

    /// <summary>Harcama itirazı (ters ibraz) — tutar işyerinden çekilir.</summary>
    Chargeback = 20,

    /// <summary>Ön hakem — işyeri kanıt sundu, banka ikinci kez itiraz etti.</summary>
    PreArbitration = 30,

    /// <summary>Hakem heyeti — karar bağlayıcıdır ve masraflıdır.</summary>
    Arbitration = 40,
}

public enum DisputeStatus
{
    /// <summary>Açıldı, kanıt bekleniyor.</summary>
    Open = 10,

    /// <summary>Kanıt bankaya iletildi, sonuç bekleniyor.</summary>
    UnderReview = 20,

    /// <summary>İşyeri savunmadan vazgeçti — tutar müşteride kalır.</summary>
    Accepted = 30,

    /// <summary>Süre doldu, kanıt sunulmadı — otomatik kayıp.</summary>
    Expired = 40,

    /// <summary>İşyeri kazandı, tutar iade edildi.</summary>
    Won = 50,

    /// <summary>İşyeri kaybetti, tutar müşteride kaldı.</summary>
    Lost = 60,
}

public static class DisputeStageMap
{
    public static readonly IReadOnlyDictionary<DisputeStage, string> ToDb =
        new Dictionary<DisputeStage, string>
        {
            [DisputeStage.Retrieval] = "retrieval",
            [DisputeStage.Chargeback] = "chargeback",
            [DisputeStage.PreArbitration] = "pre_arbitration",
            [DisputeStage.Arbitration] = "arbitration",
        };

    public static readonly IReadOnlyDictionary<string, DisputeStage> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

public static class DisputeStatusMap
{
    public static readonly IReadOnlyDictionary<DisputeStatus, string> ToDb =
        new Dictionary<DisputeStatus, string>
        {
            [DisputeStatus.Open] = "open",
            [DisputeStatus.UnderReview] = "under_review",
            [DisputeStatus.Accepted] = "accepted",
            [DisputeStatus.Expired] = "expired",
            [DisputeStatus.Won] = "won",
            [DisputeStatus.Lost] = "lost",
        };

    public static readonly IReadOnlyDictionary<string, DisputeStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static bool IsFinal(DisputeStatus status)
        => status is DisputeStatus.Won or DisputeStatus.Lost
            or DisputeStatus.Accepted or DisputeStatus.Expired;
}

/// <summary>
/// Birleşik itiraz nedeni. Bankalar ve şemalar farklı kod kullanır (Visa 13.1,
/// Mastercard 4853, banka içi kodlar…); işyeri tek sözlük görsün diye indirgenir.
/// Ham kod <see cref="Dispute.RawReasonCode"/> alanında saklı kalır — savunma
/// hazırlarken şemanın kendi kuralına bakmak gerekir.
/// </summary>
public static class DisputeReasons
{
    /// <summary>"Bu işlemi ben yapmadım" — 3DS'siz işlemlerin baş belası.</summary>
    public const string Fraud = "poyra.dispute.fraud";

    /// <summary>Ürün/hizmet teslim edilmedi.</summary>
    public const string NotReceived = "poyra.dispute.product_not_received";

    /// <summary>Ürün tarife uymuyor / kusurlu.</summary>
    public const string NotAsDescribed = "poyra.dispute.product_not_as_described";

    /// <summary>Aynı işlem iki kez çekilmiş.</summary>
    public const string Duplicate = "poyra.dispute.duplicate";

    /// <summary>Tutar yanlış işlenmiş.</summary>
    public const string IncorrectAmount = "poyra.dispute.incorrect_amount";

    /// <summary>İptal/iade sözü verilmiş ama yapılmamış.</summary>
    public const string CreditNotProcessed = "poyra.dispute.credit_not_processed";

    /// <summary>Abonelik iptal edilmesine rağmen çekim sürmüş.</summary>
    public const string SubscriptionCancelled = "poyra.dispute.subscription_cancelled";

    /// <summary>Yetkisiz/tanımlanamayan işlem (dolandırıcılık iddiası olmadan).</summary>
    public const string Unrecognized = "poyra.dispute.unrecognized";

    public const string Other = "poyra.dispute.other";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Fraud, NotReceived, NotAsDescribed, Duplicate, IncorrectAmount,
        CreditNotProcessed, SubscriptionCancelled, Unrecognized, Other,
    };
}


public sealed class Dispute : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }

    /// <summary>DB'de üretilen dış kimlik: dsp_…</summary>
    public string PublicId { get; private set; } = null!;

    /// <summary>İtiraza konu ödeme (Payments modülünün iç kimliği).</summary>
    public Guid PaymentIntentId { get; init; }

    /// <summary>İşyerinin gördüğü ödeme kimliği — modüller arası okuma kolaylığı için kopya.</summary>
    public required string PaymentPublicId { get; init; }

    /// <summary>Bankanın kendi itiraz dosya numarası — yazışmanın anahtarı.</summary>
    public string? ConnectorDisputeId { get; init; }

    public string? ConnectorKey { get; init; }

    /// <summary>İtiraz edilen tutar. Kısmi itiraz mümkündür (ödemenin tamamı olmayabilir).</summary>
    public long AmountMinor { get; init; }
    public string Currency { get; init; } = "TRY";

    public required string Reason { get; init; }
    public string? RawReasonCode { get; init; }

    public DisputeStage Stage { get; private set; } = DisputeStage.Chargeback;
    public DisputeStatus Status { get; private set; } = DisputeStatus.Open;

    public DateTimeOffset OpenedAt { get; init; }

    public DateTimeOffset EvidenceDueAt { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>İşyerinin savunma metni — dosyalarla birlikte bankaya gider.</summary>
    public string? EvidenceSummary { get; private set; }

    /// <summary>Süre uyarısı gönderildi mi (aynı uyarı her turda tekrar gitmesin).</summary>
    public DateTimeOffset? DueWarningSentAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static Dispute Open(
        Guid tenantId,
        Guid paymentIntentId,
        string paymentPublicId,
        long amountMinor,
        string currency,
        string reason,
        DisputeStage stage,
        DateTimeOffset openedAt,
        DateTimeOffset evidenceDueAt,
        string? connectorDisputeId,
        string? connectorKey,
        string? rawReasonCode)
        => new()
        {
            TenantId = tenantId,
            PaymentIntentId = paymentIntentId,
            PaymentPublicId = paymentPublicId,
            AmountMinor = amountMinor,
            Currency = currency,
            Reason = reason,
            Stage = stage,
            OpenedAt = openedAt,
            EvidenceDueAt = evidenceDueAt,
            ConnectorDisputeId = connectorDisputeId,
            ConnectorKey = connectorKey,
            RawReasonCode = rawReasonCode,
        };

    public bool IsOverdue(DateTimeOffset now) => Status == DisputeStatus.Open && now > EvidenceDueAt;

    /// <summary>Kalan süre — negatifse süre geçmiştir.</summary>
    public TimeSpan Remaining(DateTimeOffset now) => EvidenceDueAt - now;

    public void Submit(DateTimeOffset now, string? summary)
    {
        RequireActionable();

        if (now > EvidenceDueAt)
            throw new PoyraException(409, "dispute.evidence_window_closed",
                "Kanıt süresi doldu — bu itiraz artık savunulamaz. "
                + "Bankanız istisna tanıdıysa dosya numarasıyla doğrudan görüşün.");

        EvidenceSummary = summary;
        SubmittedAt = now;
        Status = DisputeStatus.UnderReview;
    }


    public void Accept(DateTimeOffset now)
    {
        RequireActionable();
        Status = DisputeStatus.Accepted;
        ClosedAt = now;
    }

    public void Close(DisputeStatus outcome, DateTimeOffset now)
    {
        if (outcome is not (DisputeStatus.Won or DisputeStatus.Lost))
            throw new PoyraException(400, "dispute.invalid_outcome",
                "Sonuç yalnız 'won' veya 'lost' olabilir.");

        if (DisputeStatusMap.IsFinal(Status))
            throw new PoyraException(409, "dispute.already_closed",
                $"İtiraz zaten sonuçlanmış ({DisputeStatusMap.ToDb[Status]}).");

        Status = outcome;
        ClosedAt = now;
    }


    public void MarkExpired(DateTimeOffset now)
    {
        if (Status != DisputeStatus.Open)
            return;

        Status = DisputeStatus.Expired;
        ClosedAt = now;
    }


    public void Escalate(DisputeStage stage, DateTimeOffset evidenceDueAt)
    {
        if (stage <= Stage)
            throw new PoyraException(409, "dispute.invalid_stage",
                "İtiraz yalnız üst kademeye taşınabilir.");

        Stage = stage;
        Status = DisputeStatus.Open;
        EvidenceDueAt = evidenceDueAt;
        SubmittedAt = null;
        DueWarningSentAt = null;
    }

    private void RequireActionable()
    {
        if (DisputeStatusMap.IsFinal(Status))
            throw new PoyraException(409, "dispute.already_closed",
                $"İtiraz sonuçlanmış ({DisputeStatusMap.ToDb[Status]}) — üzerinde işlem yapılamaz.");

        if (Status == DisputeStatus.UnderReview)
            throw new PoyraException(409, "dispute.already_submitted",
                "Kanıt zaten iletildi; bankanın kararı bekleniyor.");
    }
}

public sealed class DisputeEvidence : ITenantOwned, IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid DisputeId { get; init; }

    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Content { get; init; }
    public int SizeBytes { get; init; }

    /// <summary>Belgenin ne olduğu: kargo takibi, teslim tutanağı, yazışma…</summary>
    public required string Kind { get; init; }

    public Guid? UploadedByUserId { get; init; }

    /// <summary>İptal edilmiş belge listede kalır ama bankaya gönderilmez.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Bir itirazda toplanabilecek belge boyutu — bankalar zaten sınırlıdır.</summary>
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxFilesPerDispute = 20;

    public static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>
    {
        "application/pdf", "image/jpeg", "image/png", "image/webp", "text/plain",
    };
}

public sealed class DisputeEvent : ITenantOwned, IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid DisputeId { get; init; }
    public required string EventType { get; init; }
    public required string Actor { get; init; }
    public string Payload { get; init; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }

    public static DisputeEvent For(Dispute dispute, string eventType, string actor, object? payload = null)
        => new()
        {
            TenantId = dispute.TenantId,
            DisputeId = dispute.Id,
            EventType = eventType,
            Actor = actor,
            Payload = payload is null ? "{}" : JsonSerializer.Serialize(payload),
        };
}
