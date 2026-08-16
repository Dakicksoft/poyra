namespace Poyra.SharedKernel.Tenancy;


public sealed class TenantContext
{
    public Guid TenantId { get; private set; }
    public Guid? ProfileId { get; private set; }
    public bool IsPlatform { get; private set; }

    public bool HasTenant => TenantId != Guid.Empty;

    public void Set(Guid tenantId, Guid? profileId = null)
    {
        TenantId = tenantId;
        ProfileId = profileId;
    }

    public void SetPlatform() => IsPlatform = true;

    public static TenantContext Platform
    {
        get
        {
            var ctx = new TenantContext();
            ctx.SetPlatform();
            return ctx;
        }
    }
}
