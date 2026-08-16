using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Compliance.Domain;

public sealed class AuditLogEntry : ITenantOwned, IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }

    /// <summary>"user:id" | "api_key" | "system" | "platform".</summary>
    public required string Actor { get; init; }

    public required string Action { get; init; }
    public required string ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public required string Summary { get; init; }

    /// <summary>Ek alanlar (jsonb). Kişisel veri ve sır KOYULMAZ.</summary>
    public string Metadata { get; init; } = "{}";

    /// <summary>İsteğin geldiği adres — "içeriden mi dışarıdan mı" sorusu.</summary>
    public string? IpAddress { get; init; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Şüpheli işlem bildiriminin (ŞİB) yaşam döngüsü.</summary>
public enum SuspiciousReportStatus
{
    /// <summary>Uyum görevlisi inceliyor.</summary>
    UnderReview = 10,

    /// <summary>İnceleme bitti, bildirime gerek görülmedi (gerekçesiyle).</summary>
    Dismissed = 20,

    /// <summary>Bildirilmeye karar verildi, dosya hazırlanıyor.</summary>
    ReadyToFile = 30,

    /// <summary>MASAK'a bildirildi.</summary>
    Filed = 40,
}

public static class SuspiciousReportStatusMap
{
    public static readonly IReadOnlyDictionary<SuspiciousReportStatus, string> ToDb =
        new Dictionary<SuspiciousReportStatus, string>
        {
            [SuspiciousReportStatus.UnderReview] = "under_review",
            [SuspiciousReportStatus.Dismissed] = "dismissed",
            [SuspiciousReportStatus.ReadyToFile] = "ready_to_file",
            [SuspiciousReportStatus.Filed] = "filed",
        };

    public static readonly IReadOnlyDictionary<string, SuspiciousReportStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

/// <summary>
/// Şüpheli işlem kaydı — MASAK'ın "şüpheli işlem bildirimi" (ŞİB) sürecinin
/// iç ayağı.
///
/// <b>Önemli sınır:</b> MASAK yükümlüsü LİSANSLI ödeme kuruluşu/bankadır. Poyra'yı
/// kendi sunucusunda çalıştıran işyeri yükümlü DEĞİLDİR; Poyra'yı SaaS olarak
/// işleten taraf ödeme kuruluşu yükümlü olur. Bu kayıt, o durumda
/// gereken iç incelemenin ve karar gerekçesinin defteridir.
///
/// TODO(lisans): MASAK'a fiili gönderim (elektronik bildirim formatı, kurum
/// erişimi) lisans ve MASAK kaydı gerektirir; burada yalnız dosya HAZIRLANIR.
/// </summary>
public sealed class SuspiciousActivityReport : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }

    /// <summary>DB'de üretilen dış kimlik: sar_…</summary>
    public string PublicId { get; private set; } = null!;

    /// <summary>İnceleme konusu ödeme(ler) — virgülle ayrılmış dış kimlikler.</summary>
    public required string PaymentIds { get; init; }

    public string? CustomerRef { get; init; }

    /// <summary>Neden şüpheli görüldü — uyum görevlisinin kendi cümlesi.</summary>
    public required string Rationale { get; set; }

    public SuspiciousReportStatus Status { get; private set; } = SuspiciousReportStatus.UnderReview;

    /// <summary>Kapanış gerekçesi (bildirildi ya da vazgeçildi).</summary>
    public string? Resolution { get; private set; }

    public Guid? OpenedByUserId { get; init; }
    public Guid? ResolvedByUserId { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public void Resolve(SuspiciousReportStatus status, string resolution, Guid? userId, DateTimeOffset now)
    {
        if (status is not (SuspiciousReportStatus.Dismissed
            or SuspiciousReportStatus.ReadyToFile or SuspiciousReportStatus.Filed))
            throw new Poyra.SharedKernel.Errors.PoyraException(400, "compliance.invalid_status",
                "Sonuç 'dismissed', 'ready_to_file' veya 'filed' olabilir.");

        if (Status is SuspiciousReportStatus.Filed or SuspiciousReportStatus.Dismissed)
            throw new Poyra.SharedKernel.Errors.PoyraException(409, "compliance.already_resolved",
                $"Kayıt zaten sonuçlanmış ({SuspiciousReportStatusMap.ToDb[Status]}).");

        // Gerekçesiz karar, denetimde savunulamaz bir karardır
        if (string.IsNullOrWhiteSpace(resolution))
            throw new Poyra.SharedKernel.Errors.PoyraException(400, "compliance.resolution_required",
                "Karar gerekçesi zorunlu.");

        Status = status;
        Resolution = resolution.Trim();
        ResolvedByUserId = userId;
        ResolvedAt = now;
    }
}
