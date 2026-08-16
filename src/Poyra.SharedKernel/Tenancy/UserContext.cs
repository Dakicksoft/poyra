namespace Poyra.SharedKernel.Tenancy;

/// <summary>
/// İşyeri içindeki roller — sayısal değer yetki sırasıdır (büyük = geniş).
/// DB'de küçük harf string saklanır (TenantRoleMap).
/// </summary>
public enum TenantRole
{
    Auditor = 10, // salt okur (denetçi)
    Finance = 40, // rapor/mutabakat; para aksiyonu yok
    Operations = 50, // ödeme/iade/iptal yapabilir
    Developer = 60, // anahtar/webhook yönetir
    Admin = 80, // bağlantı/rota/kullanıcı yönetir
    Owner = 100,
}

public static class TenantRoleMap
{
    public static readonly IReadOnlyDictionary<TenantRole, string> ToDb =
        new Dictionary<TenantRole, string>
        {
            [TenantRole.Auditor] = "auditor",
            [TenantRole.Finance] = "finance",
            [TenantRole.Operations] = "operations",
            [TenantRole.Developer] = "developer",
            [TenantRole.Admin] = "admin",
            [TenantRole.Owner] = "owner",
        };

    public static readonly IReadOnlyDictionary<string, TenantRole> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

public sealed class UserContext
{
    public Guid? UserId { get; private set; }
    public string? Email { get; private set; }
    public TenantRole? Role { get; private set; }
    public bool IsMachine { get; private set; }

    public void SetUser(Guid userId, string email, TenantRole role)
    {
        UserId = userId;
        Email = email;
        Role = role;
    }

    public void SetMachine() => IsMachine = true;

    public bool HasRank(TenantRole minimum)
        => IsMachine || (Role.HasValue && Role.Value >= minimum);
}
