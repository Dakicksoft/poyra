using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Customers.Domain;

/// <summary>
/// İşyerinin müşterisi.
///
/// <b>Anahtar işyerinin kendi referansıdır</b> (<see cref="Ref"/>), Poyra'nın ürettiği
/// bir kimlik değil: <c>customerRef</c> bugün ödemede, Kasa'da, abonelikte ve risk
/// hız sayaçlarında zaten dolaşıyor. Yeni bir kimliğe geçirmek, çalışan entegrasyonları
/// kırmak olurdu; bu kayıt var olan referansa BAĞLANIR.
///
/// <b>KVKK:</b> ad, e-posta ve telefon kişisel veridir. Silme hakkı ile mali kayıt
/// saklama yükümlülüğü (VUK 5 yıl, TTK 10 yıl) çatışır; çözüm <see cref="Erase"/>
/// yöntemindedir — kişisel veri silinir, işlem kaydı durur.
/// </summary>
public sealed class Customer : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }

    /// <summary>İşyerinin müşteri anahtarı — ödeme/abonelik/kasa bu değerle bağlanır.</summary>
    public required string Ref { get; init; }

    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>İşyerinin serbest notu. Kişisel veri içerebilir — silmede temizlenir.</summary>
    public string? Notes { get; set; }

    /// <summary>KVKK silme talebi uygulandıysa zamanı.</summary>
    public DateTimeOffset? ErasedAt { get; private set; }

    public Guid? ErasedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsErased => ErasedAt is not null;

    /// <summary>
    /// KVKK silme talebi.
    ///
    /// Kayıt SİLİNMEZ, kişisel veri silinir. Gerekçe: müşterinin silme hakkı ile
    /// mali kayıt saklama yükümlülüğü çatışır. Ödemeyi silmek yasal olarak mümkün
    /// değildir; ama ödemeyi bir İSME bağlayan alanları silmek mümkündür ve
    /// KVKK'nın istediği de budur.
    ///
    /// <c>Ref</c> KORUNUR: ödeme, abonelik ve kasa kayıtları ona bağlıdır ve
    /// silinmesi mali defteri kopararak yeni bir uyum sorunu yaratırdı. Referans
    /// işyerinin kendi anahtarıdır; tek başına kimliği açığa çıkarmaz.
    /// </summary>
    public void Erase(Guid? userId, DateTimeOffset now)
    {
        if (IsErased)
            throw new PoyraException(409, "customer.already_erased",
                "Bu müşterinin kişisel verisi zaten silinmiş.");

        Name = null;
        Email = null;
        Phone = null;
        Notes = null;
        ErasedAt = now;
        ErasedByUserId = userId;
    }

    public void Update(string? name, string? email, string? phone, string? notes)
    {
        if (IsErased)
            throw new PoyraException(409, "customer.erased",
                "Kişisel verisi silinmiş müşteri güncellenemez — "
                + "yeniden veri girmek silme talebini geçersiz kılardı.");

        Name = Trim(name);
        Email = TurkishText.FoldOrNull(email);
        Phone = Trim(phone);
        Notes = Trim(notes);
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Tekrarlayan ödeme talimatının türü.</summary>
public enum MandateType
{
    /// <summary>Abonelik: sabit aralıkla, işyeri başlatır.</summary>
    Recurring = 10,

    /// <summary>Kayıtlı kartla tek tık: müşteri başlatır, tutar değişir.</summary>
    CardOnFile = 20,
}

public static class MandateTypeMap
{
    public static readonly IReadOnlyDictionary<MandateType, string> ToDb =
        new Dictionary<MandateType, string>
        {
            [MandateType.Recurring] = "recurring",
            [MandateType.CardOnFile] = "card_on_file",
        };

    public static readonly IReadOnlyDictionary<string, MandateType> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

/// <summary>
/// Ödeme talimatı — müşterinin tekrarlayan çekime verdiği ONAY kaydı.
///
/// Bugün abonelik kayıtlı kartla çekim yapıyor ama "müşteri buna razı oldu"
/// kaydı hiçbir yerde yok. Bu kayıt iki yerde işe yarar:
///  ① <b>İtiraz savunması (M10):</b> "yetkisiz işlem" iddiasına karşı sunulacak
///    ilk belge, onayın kendisidir — ne zaman, hangi metinle, hangi IP'den.
///  ② Müşterinin onayı geri çekmesi (iptal) tek yerden izlenir.
///
/// Talimat SİLİNMEZ, iptal edilir (İlke 3): iptalden önceki çekimlerin dayanağı
/// sonradan sorulur.
/// </summary>
public sealed class Mandate : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }

    /// <summary>DB'de üretilen dış kimlik: mnd_…</summary>
    public string PublicId { get; private set; } = null!;

    public required string CustomerRef { get; init; }

    /// <summary>Onayın verildiği kart (Kasa token'ı).</summary>
    public required string CardToken { get; init; }

    public MandateType Type { get; init; } = MandateType.Recurring;

    /// <summary>Müşterinin kabul ettiği metnin sürümü — metin değişirse yeni onay gerekir.</summary>
    public required string TextVersion { get; init; }

    public DateTimeOffset AcceptedAt { get; init; }

    /// <summary>Onayın verildiği IP — savunmada "kim, nereden" sorusu sorulur.</summary>
    public string? AcceptedIp { get; init; }

    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsActive => RevokedAt is null;

    public void Revoke(string reason, DateTimeOffset now)
    {
        if (!IsActive)
            throw new PoyraException(409, "mandate.already_revoked", "Talimat zaten iptal edilmiş.");

        RevokedAt = now;
        RevokedReason = string.IsNullOrWhiteSpace(reason) ? "Müşteri talebi." : reason.Trim();
    }
}
