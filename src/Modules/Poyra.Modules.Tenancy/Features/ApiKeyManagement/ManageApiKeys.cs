using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Tenancy.Domain;
using Poyra.Modules.Tenancy.Security;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Tenancy.Features.ApiKeyManagement;

/// <param name="PrefixHint">Anahtarın ilk 12 karakteri — hangi anahtar olduğunu ayırt eder.</param>
public sealed record ApiKeyDto(
    Guid Id,
    string Name,
    string PrefixHint,
    bool Revoked,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt);

public sealed record CreatedApiKeyDto(Guid Id, string Name, string PrefixHint, string Key);

public sealed record ListApiKeysQuery : IQuery<IReadOnlyList<ApiKeyDto>>;

public sealed class ListApiKeysHandler(TenancyDbContext db, TenantContext tenant)
    : IQueryHandler<ListApiKeysQuery, IReadOnlyList<ApiKeyDto>>
{
    public async Task<IReadOnlyList<ApiKeyDto>> Handle(ListApiKeysQuery query, CancellationToken ct)
        => await db.ApiKeys.AsNoTracking()
            .Where(k => k.TenantId == tenant.TenantId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeyDto(
                k.Id, k.Name, k.PrefixHint, k.RevokedAt != null, k.CreatedAt, k.RevokedAt))
            .ToListAsync(ct);
}

public sealed class ListApiKeysEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<IReadOnlyList<ApiKeyDto>>
{
    public override void Configure()
    {
        Get("/v1/api-keys");
        Description(x => x.WithTags("Tenancy"));
        Summary(s => s.Summary = "İşyerinin API anahtarları (düz değer DÖNMEZ, yalnız önek).");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListApiKeysQuery(), ct), ct);
}

public sealed record CreateApiKeyRequest(string Name, bool Live = false);

public sealed record CreateApiKeyCommand(string Name, bool Live)
    : Poyra.SharedKernel.Cqrs.ICommand<CreatedApiKeyDto>;

public sealed class CreateApiKeyValidator : AbstractValidator<CreateApiKeyCommand>
{
    public CreateApiKeyValidator()
        => RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(100)
            .WithMessage("Anahtara ayırt edici bir ad verin (ör. 'Üretim sunucusu').");
}

public sealed class CreateApiKeyHandler(TenancyDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreateApiKeyCommand, CreatedApiKeyDto>
{
    public const int MaxActiveKeys = 10;

    public async Task<CreatedApiKeyDto> Handle(CreateApiKeyCommand command, CancellationToken ct)
    {
        var active = await db.ApiKeys
            .CountAsync(k => k.TenantId == tenant.TenantId && k.RevokedAt == null, ct);

        if (active >= MaxActiveKeys)
            throw PoyraException.Conflict("api_key.too_many",
                $"En çok {MaxActiveKeys} aktif anahtar olabilir — kullanılmayanları iptal edin.");

        var plain = ApiKeys.Generate(command.Live);
        var record = new ApiKey
        {
            TenantId = tenant.TenantId,
            Name = command.Name.Trim(),
            PrefixHint = ApiKeys.PrefixHint(plain),
            KeyHash = ApiKeys.Hash(plain),
        };

        db.ApiKeys.Add(record);
        await db.SaveChangesAsync(ct);

        return new CreatedApiKeyDto(record.Id, record.Name, record.PrefixHint, plain);
    }
}

public sealed class CreateApiKeyEndpoint(IDispatcher dispatcher) : Endpoint<CreateApiKeyRequest, CreatedApiKeyDto>
{
    public override void Configure()
    {
        Post("/v1/api-keys");
        Description(x => x.WithTags("Tenancy"));
        Summary(s => s.Summary =
            "Yeni API anahtarı üretir. Düz değer YALNIZ bu yanıtta döner — kaydedin.");
    }

    public override async Task HandleAsync(CreateApiKeyRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new CreateApiKeyCommand(req.Name, req.Live), ct), ct);
}

public sealed record RevokeApiKeyCommand(Guid KeyId) : Poyra.SharedKernel.Cqrs.ICommand<ApiKeyDto>;

public sealed class RevokeApiKeyHandler(TenancyDbContext db, TenantContext tenant, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<RevokeApiKeyCommand, ApiKeyDto>
{
    public async Task<ApiKeyDto> Handle(RevokeApiKeyCommand command, CancellationToken ct)
    {
        var record = await db.ApiKeys.SingleOrDefaultAsync(
                k => k.Id == command.KeyId && k.TenantId == tenant.TenantId, ct)
            ?? throw PoyraException.NotFound("api_key.not_found", "API anahtarı bulunamadı.");

        if (record.RevokedAt is null)
        {
            var otherActive = await db.ApiKeys.CountAsync(
                k => k.TenantId == tenant.TenantId && k.RevokedAt == null && k.Id != record.Id, ct);

            if (otherActive == 0)
                throw PoyraException.Conflict("api_key.last_active",
                    "Son aktif anahtar iptal edilemez — önce yeni bir anahtar üretin, "
                    + "sunucunuza dağıtın, sonra bunu kapatın.");

            record.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return new ApiKeyDto(record.Id, record.Name, record.PrefixHint, true,
            record.CreatedAt, record.RevokedAt);
    }
}

public sealed class RevokeApiKeyEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<ApiKeyDto>
{
    public override void Configure()
    {
        Post("/v1/api-keys/{id}/revoke");
        Description(x => x.WithTags("Tenancy"));
        Summary(s => s.Summary =
            "Anahtarı iptal eder (kayıt silinmez). Son aktif anahtar iptal edilemez.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new RevokeApiKeyCommand(Route<Guid>("id")), ct), ct);
}
