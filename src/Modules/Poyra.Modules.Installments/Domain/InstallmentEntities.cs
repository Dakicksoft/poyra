using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Installments.Domain;

public sealed class CardBin : IAuditable
{
    public required string Bin { get; init; } // 6-8 hane, PK
    public required string BankCode { get; set; } // ör. "0062" (Garanti), "0064" (İş)
    public required string BankName { get; set; }
    public required string Program { get; set; } // bonus/world/maximum/axess/paraf/cardfinans/advantage/bankkart/other
    public required string Brand { get; set; } // visa/mastercard/troy/amex
    public required string CardType { get; set; } // credit/debit/prepaid
    public bool IsCommercial { get; set; }

    /// <summary>
    /// Kartı çıkaran ülkenin ISO-3166 alpha-2 kodu (TR, DE, US…). Varsayılan "TR":
    /// katalog Türkiye BIN'leriyle doldurulur ve yurt dışı kart burada AÇIKÇA işaretlenir.
    /// "Katalogda yok = yabancı" varsayımı yapılamaz — eksik katalog kaydıyla gerçekten
    /// yabancı kart aynı görünürdü; ülke kuralı o zaman TR kartlarını da yakalardı.
    /// </summary>
    public string Country { get; set; } = "TR";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// İşyerinin taksit şeması: hangi POS hesabında, hangi kart programına, kaç taksit,
/// yüzde kaç müşteri vade farkı (bps — ‱). Program "*" tüm programlara uygulanır.
/// Silinmez; pasife çekilir.
/// </summary>
public sealed class InstallmentScheme : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid ConnectorAccountId { get; init; }
    public required string Program { get; set; } // "*" = tüm programlar
    public int InstallmentCount { get; set; } // 2-12 (1 = tek çekim, şema gerektirmez)
    public int CustomerRateBps { get; set; } // 0 = vade farksız; 350 = %3,50
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
