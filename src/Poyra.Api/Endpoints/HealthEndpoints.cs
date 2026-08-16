using System.Reflection;
using FastEndpoints;
using Poyra.Modules.Tenancy;

namespace Poyra.Api.Endpoints;

public sealed record HealthResponse(string Status, string Version);

internal static class BuildInfo
{
    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}

public sealed class LiveEndpoint : EndpointWithoutRequest<HealthResponse>
{
    public override void Configure()
    {
        Get("/health/live");
        Description(x => x.WithTags("Platform"));
    }

    public override Task HandleAsync(CancellationToken ct)
        => Send.OkAsync(new HealthResponse("ok", BuildInfo.Version), ct);
}

public sealed class ReadyEndpoint(TenancyDbContext db) : EndpointWithoutRequest<HealthResponse>
{
    public override void Configure()
    {
        Get("/health/ready");
        Description(x => x.WithTags("Platform"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var canConnect = await db.Database.CanConnectAsync(ct);
        if (!canConnect)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await HttpContext.Response.WriteAsJsonAsync(
                new HealthResponse("db_unreachable", BuildInfo.Version), ct);
            return;
        }

        await Send.OkAsync(new HealthResponse("ok", BuildInfo.Version), ct);
    }
}
