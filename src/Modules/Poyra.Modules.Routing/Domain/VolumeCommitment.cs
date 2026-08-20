using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Routing.Domain;

/// <summary>
/// "Bu bankaya ayda X ₺ ciro sözü verdim." Türkiye'de daha iyi komisyon oranı genelde
/// hacim taahhüdü karşılığında alınır; taahhüt tutmazsa oran geri yükselir. Taahhüdü
/// takip etmemek, ay sonunda farkında olmadan pahalı orana düşmek demektir.
///
/// <b>Neden Routing modülünde:</b> komisyon anlaşması (Recon) orada durur çünkü Recon'un
/// KENDİSİ onu denetler — ekstreyle karşılaştırır. Taahhüdü ise yalnız rota okur; başka
/// hiçbir modülün işine yaramaz. Veriyi tek tüketicisinin yanında tutmak, modüller arası
/// gereksiz bir port daha açmaktan iyidir.
///
/// Dönem AYLIKTIR ve Türkiye takvimine göre hesaplanır (UTC+3): bankayla yapılan
/// anlaşmalar ay bazlıdır, hesap kesim tarihi değil.
/// </summary>
public sealed class VolumeCommitment : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }

    public Guid ConnectorAccountId { get; init; }

    /// <summary>Aylık hedef ciro (kuruş).</summary>
    public long MonthlyTargetMinor { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
