using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Poyra.Panel.Security;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Panel.Components;

public abstract class TenantScopedComponent : OwningComponentBase
{
    [CascadingParameter]
    private Task<AuthenticationState>? AuthState { get; set; }

    protected IDispatcher Dispatcher => ScopedServices.GetRequiredService<IDispatcher>();

    protected Guid TenantId { get; private set; }
    protected TenantRole? Role { get; private set; }

    protected bool HasRank(TenantRole minimum) => Role is { } role && role >= minimum;

    protected async Task<bool> EnsureTenantAsync()
    {
        if (TenantId == Guid.Empty)
        {
            if (AuthState is null)
                return false;

            var state = await AuthState;
            if (!Guid.TryParse(state.User.FindFirstValue(PanelClaims.TenantId), out var tenantId))
                return false;

            TenantId = tenantId;
            Role = TenantRoleMap.FromDb.TryGetValue(
                state.User.FindFirstValue(PanelClaims.Role) ?? "", out var role) ? role : null;
        }

        ScopedServices.GetRequiredService<TenantContext>().Set(TenantId);
        return true;
    }
}
