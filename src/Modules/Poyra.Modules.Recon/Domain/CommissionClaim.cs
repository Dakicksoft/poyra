using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Recon.Domain;

public enum ClaimStatus
{
    /// <summary>Bulgular toplandı, henüz bankaya iletilmedi.</summary>
    Draft = 10,

    /// <summary>Bankaya iletildi, cevap bekleniyor.</summary>
    Submitted = 20,

    /// <summary>Banka kabul etti — düzeltme/iade bekleniyor.</summary>
    Accepted = 30,

    /// <summary>Banka reddetti. Gerekçe kayıtlıdır; kapatılır ama SİLİNMEZ.</summary>
    Rejected = 40,

    /// <summary>Kısmen kabul edildi — talep edilenin bir kısmı geri geldi.</summary>
    PartiallyRecovered = 50,

    /// <summary>Kapandı: para geri alındı ya da takip sonlandırıldı.</summary>
    Closed = 60,
}

public static class ClaimStatusMap
{
    public static readonly IReadOnlyDictionary<ClaimStatus, string> ToDb =
        new Dictionary<ClaimStatus, string>
        {
            [ClaimStatus.Draft] = "draft",
            [ClaimStatus.Submitted] = "submitted",
            [ClaimStatus.Accepted] = "accepted",
            [ClaimStatus.Rejected] = "rejected",
            [ClaimStatus.PartiallyRecovered] = "partially_recovered",
            [ClaimStatus.Closed] = "closed",
        };

    public static readonly IReadOnlyDictionary<string, ClaimStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}


public sealed class CommissionClaim : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public string PublicId { get; private set; } = null!; // clm_…

    public Guid ConnectorAccountId { get; init; }

    /// <summary>Talebin kapsadığı dönem — banka bu aralıkla muhatap olur.</summary>
    public DateOnly PeriodFrom { get; init; }
    public DateOnly PeriodTo { get; init; }

    /// <summary>Talep edilen toplam (fazla kesilen komisyon).</summary>
    public long ClaimedMinor { get; init; }

    /// <summary>Talebin dayandığı bulgu sayısı — bankaya sunulan kanıtın hacmi.</summary>
    public int FindingCount { get; init; }

    /// <summary>
    /// Gerçekten geri alınan. <b>Kısmi tahsilat normaldir</b> — banka bazen bir
    /// kısmını kabul eder. Talep edilenle karıştırılırsa "geri aldık" rakamı şişer.
    /// </summary>
    public long RecoveredMinor { get; set; }

    public ClaimStatus Status { get; set; } = ClaimStatus.Draft;

    public string? BankReference { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Hâlâ tahsil edilmemiş tutar.</summary>
    public long OutstandingMinor => ClaimedMinor - RecoveredMinor;

    /// <summary>
    /// Tahsilat oranından duruma karar verir.
    ///
    /// Tamamı geldiyse kapanır; bir kısmı geldiyse <b>kapanmaz</b> — kalanı
    /// unutmamak için açık kalması gerekir.
    /// </summary>
    public static ClaimStatus StatusAfterRecovery(long claimedMinor, long recoveredMinor)
        => recoveredMinor <= 0 ? ClaimStatus.Submitted
            : recoveredMinor >= claimedMinor ? ClaimStatus.Closed
            : ClaimStatus.PartiallyRecovered;
}

public sealed class CommissionClaimEvent : ITenantOwned, IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid ClaimId { get; init; }

    /// <summary>submitted · bank_responded · recovered · rejected · closed · note</summary>
    public required string EventType { get; init; }

    public string? Note { get; init; }

    public long? AmountMinor { get; init; }

    public Guid? ActorUserId { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
}
