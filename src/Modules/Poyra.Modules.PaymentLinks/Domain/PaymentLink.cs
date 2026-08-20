using System.Buffers.Text;
using System.Security.Cryptography;
using Poyra.SharedKernel.Domain;

using Poyra.Modules.PaymentLinks.Contracts;

namespace Poyra.Modules.PaymentLinks.Domain;

public enum PaymentLinkStatus
{
    Active,
    Disabled,
}

public static class PaymentLinkStatusMap
{
    public static readonly IReadOnlyDictionary<PaymentLinkStatus, string> ToDb =
        new Dictionary<PaymentLinkStatus, string>
        {
            [PaymentLinkStatus.Active] = "active",
            [PaymentLinkStatus.Disabled] = "disabled",
        };

    public static readonly IReadOnlyDictionary<string, PaymentLinkStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

public sealed class PaymentLink : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public string PublicId { get; private set; } = null!; // lnk_…

    /// <summary>Kısa, tahmin edilemez URL parçası — checkout adresinin gövdesi.</summary>
    public required string Slug { get; init; }

    public long? AmountMinor { get; init; }
    public string Currency { get; init; } = "TRY";
    public required string Description { get; set; }
    public int MaxInstallments { get; init; } = 1;
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>0 = sınırsız. Aşıldığında link kapanır (tek kullanımlık link için 1).</summary>
    public int MaxUsage { get; init; }

    /// <summary>
    /// Bağlantıyı kimin ürettiği (bkz. PaymentLinkOrigins): panel/API mi, saha uygulaması mı.
    /// Ödemenin kanalına taşınır — rota "saha tahsilatı şu POS'a gitsin" diyebilsin diye.
    /// Alan eklenmeden önceki bağlantılar varsayılan "link" sayılır: saha bağlantıları o
    /// dönemde de azınlıktı ve yanlış tarafa düşen kayıt yalnız kanal kuralını etkiler.
    /// </summary>
    public string Origin { get; init; } = PaymentLinkOrigins.Link;

    public int SuccessCount { get; set; }
    public PaymentLinkStatus Status { get; set; } = PaymentLinkStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static string NewSlug()
        // 12 bayt = 96 bit; kaba kuvvetle bulunamaz, URL'de kısa durur
        => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12));

    public bool IsOpenAmount => AmountMinor is null;

    public string? UnavailableReason(DateTimeOffset now)
    {
        if (Status != PaymentLinkStatus.Active)
            return "Bu ödeme bağlantısı kapatılmış.";
        if (ExpiresAt is { } expiry && expiry <= now)
            return "Bu ödeme bağlantısının süresi dolmuş.";
        if (MaxUsage > 0 && SuccessCount >= MaxUsage)
            return "Bu ödeme bağlantısı kullanım sınırına ulaşmış.";

        return null;
    }
}


public sealed class PaymentLinkLookup : IHasCreatedAt
{
    public required string Slug { get; init; }
    public Guid TenantId { get; init; }
    public Guid PaymentLinkId { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PaymentLinkUsage : ITenantOwned, IHasCreatedAt
{
    public required string PaymentPublicId { get; init; } // pay_…
    public Guid TenantId { get; init; }
    public Guid PaymentLinkId { get; init; }
    public long AmountMinor { get; init; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PaymentLinkAttempt : ITenantOwned, IHasCreatedAt
{
    public required string PaymentPublicId { get; init; } // pay_…
    public Guid TenantId { get; init; }
    public Guid PaymentLinkId { get; init; }
    public long AmountMinor { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
}
