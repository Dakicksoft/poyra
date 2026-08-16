using Poyra.SharedKernel.Audit;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Api.Middleware;

public sealed class AuditMiddleware(RequestDelegate next)
{
    private static readonly (string Prefix, string Resource, string Title)[] Tracked =
    [
        ("/v1/connector-accounts", "connector_account", "POS hesabı"),
        ("/v1/routing", "routing_rule", "Rota kuralı"),
        ("/v1/risk/rules", "risk_rule_set", "Risk kural seti"),
        ("/v1/risk/blocklist", "blocklist_entry", "Kara liste"),
        ("/v1/users", "user", "Kullanıcı"),
        ("/v1/api-keys", "api_key", "API anahtarı"),
        ("/v1/branding", "branding", "Marka ayarı"),
        ("/v1/installments/schemes", "installment_scheme", "Taksit şeması"),
        ("/v1/webhook-endpoints", "webhook_endpoint", "Webhook ucu"),
        ("/v1/refunds", "refund", "İade"),
        ("/v1/disputes", "dispute", "İtiraz"),
        ("/v1/vault", "card_token", "Kasa kartı"),
        ("/v1/plans", "plan", "Abonelik planı"),
        ("/v1/subscriptions", "subscription", "Abonelik"),
        ("/v1/recon", "recon", "Mutabakat"),
        ("/v1/settlements", "settlement", "Hesap ekstresi"),
        ("/v1/ledger", "ledger", "Defter"),
        ("/v1/compliance", "compliance", "Uyum kaydı"),
        ("/v1/customers", "customer", "Müşteri"),
        ("/v1/mandates", "mandate", "Ödeme talimatı"),
        ("/v1/field/agents", "field_agent", "Saha temsilcisi"),
    ];

    public async Task InvokeAsync(HttpContext context, TenantContext tenant, UserContext user)
    {
        var match = Match(context.Request);
        if (match is null)
        {
            await next(context);
            return;
        }

        await next(context);

        var status = context.Response.StatusCode;
        var succeeded = status is >= 200 and < 300;

        if (!succeeded && status != StatusCodes.Status403Forbidden)
            return;

        if (!tenant.HasTenant)
            return;

        var (resource, title) = match.Value;
        var verb = Verb(context.Request);

        var action = succeeded ? $"{resource}.{verb}" : $"{resource}.denied";
        var summary = succeeded
            ? $"{title}: {context.Request.Method} {context.Request.Path}"
            : $"{title}: YETKİSİZ deneme ({context.Request.Method} {context.Request.Path})";

        var audit = context.RequestServices.GetRequiredService<IAuditTrail>();
        await audit.RecordAsync(new AuditEvent(
            Actor(user),
            action,
            resource,
            ResourceIdFromPath(context.Request.Path),
            summary,
            new Dictionary<string, string>
            {
                ["method"] = context.Request.Method,
                ["path"] = context.Request.Path.ToString(),
                ["status"] = status.ToString(),
            },
            context.Connection.RemoteIpAddress?.ToString()), context.RequestAborted);
    }


    private static string Verb(HttpRequest request)
    {
        var last = request.Path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();

        if (last is not null && SubActions.Contains(last))
            return last.Replace('-', '_');

        return request.Method switch
        {
            "POST" => "created",
            "PUT" or "PATCH" => "updated",
            "DELETE" => "deleted",
            _ => "changed",
        };
    }

    private static readonly HashSet<string> SubActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "erase", "revoke", "cancel", "confirm", "confirm-direct", "submit", "resolve",
        "accept", "escalate", "close", "replay", "rotate-secret", "status", "role",
        "publish", "probe", "test", "disable", "card", "evidence", "upload", "match", "sms",
        "settings", "release-device",
    };

    private static (string Resource, string Title)? Match(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            return null;

        foreach (var (prefix, resource, title) in Tracked)
        {
            if (request.Path.StartsWithSegments(prefix))
                return (resource, title);
        }

        return null;
    }

    private static string? ResourceIdFromPath(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return segments.FirstOrDefault(s =>
            s.Length >= 20 || Guid.TryParse(s, out _));
    }

    internal static string Actor(UserContext user)
        => user.UserId is { } id ? $"user:{id}" : "api_key";
}
