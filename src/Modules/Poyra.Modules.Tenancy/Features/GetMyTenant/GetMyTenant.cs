using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Tenancy.Features.GetMyTenant;

public sealed record GetMyTenantQuery : IQuery<TenantSummaryResponse?>;

public sealed record TenantSummaryResponse(
    Guid TenantId,
    Guid OrganizationId,
    string Name,
    string Slug,
    string Status,
    DateTimeOffset CreatedAt);

public sealed class GetMyTenantHandler(TenancyDbContext db, TenantContext tenant)
    : IQueryHandler<GetMyTenantQuery, TenantSummaryResponse?>
{
    public async Task<TenantSummaryResponse?> Handle(GetMyTenantQuery query, CancellationToken ct)
        => await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenant.TenantId)
            .Select(t => new TenantSummaryResponse(
                t.Id, t.OrganizationId, t.Name, t.Slug, t.Status.ToString().ToLowerInvariant(), t.CreatedAt))
            .SingleOrDefaultAsync(ct);
}

public sealed class GetMyTenantEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<TenantSummaryResponse>
{
    public override void Configure()
    {
        Get("/v1/tenants/me");
        Description(x => x.WithTags("Tenancy"));
        Summary(s => s.Summary = "API anahtarının ait olduğu işyerini döner.");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await dispatcher.Ask(new GetMyTenantQuery(), ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(result, ct);
    }
}
