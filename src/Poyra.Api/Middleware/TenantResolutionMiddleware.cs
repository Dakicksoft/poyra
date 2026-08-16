using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Tenancy.Infrastructure;
using Poyra.Modules.Tenancy.Security;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Api.Middleware;

public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string ApiKeyHeader = "X-Api-Key";
    public const string PlatformKeyHeader = "X-Platform-Key";
    public const string DevTenantHeader = "X-Tenant-Id";

    private static readonly (string Prefix, TenantRole Minimum)[] WriteRoleRules =
    [
        ("/v1/connector-accounts", TenantRole.Admin),
        ("/v1/routing", TenantRole.Admin),
        ("/v1/branding", TenantRole.Admin),
        ("/v1/users", TenantRole.Admin),
        ("/v1/api-keys", TenantRole.Owner),
        ("/v1/webhook-endpoints", TenantRole.Developer),
        ("/v1/webhook-deliveries", TenantRole.Developer),
        ("/v1/installments/schemes", TenantRole.Admin),
        ("/v1/recon", TenantRole.Finance),
        ("/v1/settlements", TenantRole.Finance),
        ("/v1/ledger", TenantRole.Finance),
        ("/v1/payments", TenantRole.Operations),
        ("/v1/refunds", TenantRole.Operations),
        ("/v1/vault", TenantRole.Operations),
        ("/v1/plans", TenantRole.Admin),
        ("/v1/subscriptions", TenantRole.Operations),
        ("/v1/payment-links", TenantRole.Operations),
        ("/v1/karekod", TenantRole.Admin),
        ("/v1/disputes", TenantRole.Operations),
        ("/v1/risk", TenantRole.Admin),
        ("/v1/compliance", TenantRole.Auditor),
        ("/v1/customers", TenantRole.Operations),
        ("/v1/mandates", TenantRole.Operations),
        ("/v1/field/agents", TenantRole.Admin),
        ("/v1/field", TenantRole.Operations),
    ];

    public async Task InvokeAsync(
        HttpContext context,
        TenantContext tenant,
        UserContext user,
        TenancyDbContext tenancy,
        JwtTokens jwt,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var path = context.Request.Path;

        if (HttpMethods.IsPost(context.Request.Method)
            && (path.Equals("/v1/tenants") || path.Equals("/v1/bins") || path.Equals("/v1/bank-holidays")))
        {
            var adminKey = configuration["Platform:AdminKey"];
            var provided = context.Request.Headers[PlatformKeyHeader].ToString();

            if (string.IsNullOrEmpty(adminKey) || !FixedTimeEquals(provided, adminKey))
            {
                await DenyAsync(context, 401, "platform_key_required", "Geçerli X-Platform-Key başlığı gerekli.");
                return;
            }

            tenant.SetPlatform();
            user.SetMachine();
            await next(context);
            return;
        }

        var apiKey = context.Request.Headers[ApiKeyHeader].ToString();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var hash = ApiKeys.Hash(apiKey);
            var record = await tenancy.ApiKeys.AsNoTracking()
                .SingleOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null, context.RequestAborted);

            if (record is null)
            {
                await DenyAsync(context, 401, "invalid_api_key", "API anahtarı geçersiz veya iptal edilmiş.");
                return;
            }

            tenant.Set(record.TenantId);
            user.SetMachine();
        }
        else if (context.Request.Headers.Authorization.ToString() is { Length: > 7 } auth
                 && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var principal = await jwt.ValidateAsync(auth["Bearer ".Length..].Trim());
            if (principal is null)
            {
                await DenyAsync(context, 401, "invalid_token", "Erişim belirteci geçersiz veya süresi dolmuş.");
                return;
            }

            tenant.Set(principal.TenantId);
            user.SetUser(principal.UserId, principal.Email, principal.Role);
        }
        else if (environment.IsDevelopment()
                 && Guid.TryParse(context.Request.Headers[DevTenantHeader], out var devTenantId))
        {
            tenant.Set(devTenantId);
            user.SetMachine();
        }

        if (RequiresTenant(path) && !tenant.HasTenant)
        {
            await DenyAsync(context, 401, "tenant_required", "Bu uç için geçerli X-Api-Key veya Bearer JWT gerekli.");
            return;
        }

        if (tenant.HasTenant && !HttpMethods.IsGet(context.Request.Method))
        {
            foreach (var (prefix, minimum) in WriteRoleRules)
            {
                if (!path.StartsWithSegments(prefix))
                    continue;

                if (!user.HasRank(minimum))
                {
                    await DenyAsync(context, 403, "insufficient_role",
                        $"Bu işlem için en az '{TenantRoleMap.ToDb[minimum]}' rolü gerekli.");
                    return;
                }

                break;
            }
        }

        if (tenant.HasTenant)
            Activity.Current?.SetTag("poyra.tenant_id", tenant.TenantId);

        await next(context);
    }

    private static bool RequiresTenant(PathString path)
        => path.StartsWithSegments("/v1/payments")
           || path.StartsWithSegments("/v1/refunds")
           || path.StartsWithSegments("/v1/connector-accounts")
           || path.StartsWithSegments("/v1/connectors")
           || path.StartsWithSegments("/v1/routing")
           || path.StartsWithSegments("/v1/webhook-endpoints")
           || path.StartsWithSegments("/v1/webhook-deliveries")
           || path.StartsWithSegments("/v1/installments")
           || path.StartsWithSegments("/v1/recon")
           || path.StartsWithSegments("/v1/settlements")
           || path.StartsWithSegments("/v1/ledger")
           || path.StartsWithSegments("/v1/receivables")
           || path.StartsWithSegments("/v1/analytics")
           || path.StartsWithSegments("/v1/branding")
           || path.StartsWithSegments("/v1/vault")
           || path.StartsWithSegments("/v1/plans")
           || path.StartsWithSegments("/v1/subscriptions")
           || path.StartsWithSegments("/v1/subscription-invoices")
           || path.StartsWithSegments("/v1/payment-links")
           || path.StartsWithSegments("/v1/karekod")
           || path.StartsWithSegments("/v1/bank-holidays")
           || path.StartsWithSegments("/v1/bins")
           || path.StartsWithSegments("/v1/users")
           || path.StartsWithSegments("/v1/api-keys")
           || path.StartsWithSegments("/v1/disputes")
           || path.StartsWithSegments("/v1/risk")
           || path.StartsWithSegments("/v1/platform")
           || path.StartsWithSegments("/v1/compliance")
           || path.StartsWithSegments("/v1/customers")
           || path.StartsWithSegments("/v1/mandates")
           || path.StartsWithSegments("/v1/field")
           || path.StartsWithSegments("/v1/auth/me")
           || path.StartsWithSegments("/v1/auth/email/send-verification")
           || path.StartsWithSegments("/v1/tenants/me");

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static Task DenyAsync(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { code, message, errors = (object?)null });
    }
}
