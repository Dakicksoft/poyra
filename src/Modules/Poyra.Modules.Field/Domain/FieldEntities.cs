using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Field.Domain;

/// <summary>
/// Saha tahsilatçısı (bayi temsilcisi, kurye, servis elemanı).
///
/// SİLİNMEZ, kapatılır: "bu tahsilatı kim yaptı" sorusu işten ayrılmış bir
/// temsilci için de sorulur — kaydı silmek geçmiş tahsilatları sahipsiz bırakırdı.
/// </summary>
public sealed class FieldAgent : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }

    public Guid? UserId { get; init; }

    /// <summary>Bayi/temsilci kodu. İşyeri içinde tekildir; sahadaki kimliktir.</summary>
    public required string Code { get; init; }

    public required string Name { get; set; }

    public string? Region { get; set; }

    public string? DeviceId { get; set; }

    public string? PinHash { get; set; }

    public DateTimeOffset? EnrolledAt { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public string? DisabledReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsActive => DisabledAt is null;
}

/// <summary>Sahada tahsilatın nasıl alındığı.</summary>
public enum FieldCollectionMethod
{
    /// <summary>Ödeme bağlantısı üretilir, müşteriye SMS/WhatsApp ile gider.</summary>
    Link = 10,

    /// <summary>Aynı bağlantının karekodu temsilcinin ekranında gösterilir.</summary>
    Qr = 20,

    /// <summary>Cihazdaki SoftPOS uygulamasına devredilir.</summary>
    SoftPosRedirect = 30,

    /// <summary>Nakit alındı BEYANI. Poyra'dan para geçmez — yalnız kayıttır.</summary>
    CashDeclared = 40,
}

public static class FieldCollectionMethodMap
{
    public static readonly IReadOnlyDictionary<FieldCollectionMethod, string> ToDb =
        new Dictionary<FieldCollectionMethod, string>
        {
            [FieldCollectionMethod.Link] = "link",
            [FieldCollectionMethod.Qr] = "qr",
            [FieldCollectionMethod.SoftPosRedirect] = "softpos_redirect",
            [FieldCollectionMethod.CashDeclared] = "cash_declared",
        };

    public static readonly IReadOnlyDictionary<string, FieldCollectionMethod> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}


public enum FieldCollectionStatus
{
    /// <summary>Cihazın ürettiği tek finansal olmayan durum: talep kaydedildi.</summary>
    PendingRequest = 10,

    /// <summary>Bağlantı/karekod üretildi, müşteriye sunuldu. Para hâlâ gelmedi.</summary>
    LinkIssued = 20,

    /// <summary>Ödeme akışı başarıyla kapandı — bu durumu YALNIZ sunucu yazar.</summary>
    Succeeded = 30,

    /// <summary>Ödeme başarısız ya da süresi doldu.</summary>
    Failed = 40,

    /// <summary>Nakit BEYANI. Fon hareketi yoktur; mutabakata girmez.</summary>
    CashDeclared = 50,

    /// <summary>Temsilci vazgeçti / müşteri ödemedi. Kayıt silinmez, iptal edilir.</summary>
    Cancelled = 60,
}

public static class FieldCollectionStatusMap
{
    public static readonly IReadOnlyDictionary<FieldCollectionStatus, string> ToDb =
        new Dictionary<FieldCollectionStatus, string>
        {
            [FieldCollectionStatus.PendingRequest] = "pending_request",
            [FieldCollectionStatus.LinkIssued] = "link_issued",
            [FieldCollectionStatus.Succeeded] = "succeeded",
            [FieldCollectionStatus.Failed] = "failed",
            [FieldCollectionStatus.CashDeclared] = "cash_declared",
            [FieldCollectionStatus.Cancelled] = "cancelled",
        };

    public static readonly IReadOnlyDictionary<string, FieldCollectionStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

public sealed class FieldCollection : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public string PublicId { get; private set; } = null!; // fc_…

    public Guid AgentId { get; init; }

    public string? CustomerRef { get; init; }

    public long AmountMinor { get; init; }
    public string Currency { get; init; } = "TRY";
    public FieldCollectionMethod Method { get; init; }

    public string? PaymentLinkId { get; set; }

    public string? CheckoutUrl { get; set; }

    public string? PaymentId { get; set; }

    public Guid ClientOpId { get; init; }

    /// <summary>Cihaz saati — BEYAN. Otorite değildir.</summary>
    public DateTimeOffset CapturedAtDevice { get; init; }

    /// <summary>Sunucu saati — YASAL zaman .</summary>
    public DateTimeOffset OccurredAtServer { get; init; }

    /// <summary>
    /// <c>OccurredAtServer - CapturedAtDevice</c>, saniye. Pozitif değer normaldir
    /// (çevrimdışı geçen süre); büyük negatif değer cihaz saatinin ileri olduğunu söyler.
    /// </summary>
    public long DeviceSkewSeconds { get; init; }

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    public FieldCollectionStatus Status { get; set; }
    public string? Note { get; init; }

    /// <summary>
    /// Cihazın ham iddiaları (jsonb).
    /// </summary>
    public string DeviceClaims { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }


    public static FieldCollectionStatus InitialStatusFor(FieldCollectionMethod method)
        => method == FieldCollectionMethod.CashDeclared
            ? FieldCollectionStatus.CashDeclared
            : FieldCollectionStatus.PendingRequest;

    /// <summary>
    /// Cihaz saatiyle sunucu saati arasındaki fark. Sunucu ileri ise pozitif.
    /// <c>TimeSpan</c> taşmasına karşı korumalı: bozuk bir cihaz saati
    /// (ör. <c>DateTimeOffset.MinValue</c>) hesaplamayı patlatmamalı.
    /// </summary>
    public static long SkewSeconds(DateTimeOffset server, DateTimeOffset device)
    {
        var seconds = (server.ToUnixTimeMilliseconds() - device.ToUnixTimeMilliseconds()) / 1000d;
        return (long)Math.Clamp(seconds, long.MinValue / 2d, long.MaxValue / 2d);
    }
}
