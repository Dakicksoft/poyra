namespace Poyra.Persistence;

/// <summary>Demo tohumlamasının sonucu — günlüğe ve testlere aynı kelimeyle döner.</summary>
public enum DemoSeedOutcome
{
    /// <summary>Bayrak kapalı; hiç bakılmadı.</summary>
    Disabled,

    /// <summary>Bayrak açık ama e-posta ya da parola verilmemiş.</summary>
    MissingSettings,

    /// <summary>Veritabanında en az bir işyeri var; hiçbir şey yazılmadı.</summary>
    TenantExists,

    /// <summary>Demo verisi kuruldu.</summary>
    Seeded,

    /// <summary>Tohumlama sırasında hata çıktı; açılış sürdürüldü.</summary>
    Failed,
}

/// <summary>
/// Poyra:Demo bölümünden okunur. Bayrak yalnız TANITIM kurulumlarında açılır:
/// veritabanı boş değilse tohumlayıcı zaten hiçbir şey yapmaz, ama bayrağı üretimde
/// açık unutmak yine de istenmez.
/// </summary>
public sealed record DemoSeedOptions
{
    public const string Section = "Poyra:Demo";

    public bool Enabled { get; init; }
    public string? Email { get; init; }
    public string? Password { get; init; }
    public string TenantName { get; init; } = "Poyra Demo";
    public string TenantSlug { get; init; } = "demo";
    public string OwnerName { get; init; } = "Demo Kullanıcı";
}
