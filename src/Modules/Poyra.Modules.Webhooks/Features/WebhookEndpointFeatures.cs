using System.Buffers.Text;
using System.Security.Cryptography;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Webhooks.Contracts;
using Poyra.Modules.Webhooks.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Security;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Webhooks.Features;

public sealed record WebhookEndpointResponse(
    Guid Id, string Url, string[] EventTypes, bool Active,
    string? Secret = null);

internal static class WebhookSecrets
{
    public static string Generate() => "whsec_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}

public sealed record CreateWebhookEndpointRequest(string Url, string[] EventTypes);

public sealed record CreateWebhookEndpointCommand(string Url, string[] EventTypes)
    : Poyra.SharedKernel.Cqrs.ICommand<WebhookEndpointResponse>;

public sealed class CreateWebhookEndpointValidator : AbstractValidator<CreateWebhookEndpointCommand>
{
    public CreateWebhookEndpointValidator()
    {
        RuleFor(x => x.Url)
            .NotEmpty().MaximumLength(2000)
            .Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .WithMessage("Webhook adresi mutlak bir http(s) adresi olmalı.");
        RuleFor(x => x.EventTypes)
            .NotEmpty()
            .Must(types => types.All(KnownWebhookEvents.All.Contains))
            .WithMessage($"Geçersiz olay tipi. Geçerli tipler: {string.Join(", ", KnownWebhookEvents.All)}");
    }
}

public sealed class CreateWebhookEndpointHandler(
    WebhooksDbContext db, TenantContext tenant, ICredentialProtector protector)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreateWebhookEndpointCommand, WebhookEndpointResponse>
{
    public async Task<WebhookEndpointResponse> Handle(CreateWebhookEndpointCommand command, CancellationToken ct)
    {
        var secret = WebhookSecrets.Generate();
        var endpoint = new WebhookEndpoint
        {
            TenantId = tenant.TenantId,
            Url = command.Url,
            EventTypes = command.EventTypes.Distinct().ToArray(),
            SecretEncrypted = protector.Protect(new Dictionary<string, string> { ["secret"] = secret }),
        };

        db.WebhookEndpoints.Add(endpoint);
        await db.SaveChangesAsync(ct);

        return new WebhookEndpointResponse(endpoint.Id, endpoint.Url, endpoint.EventTypes, endpoint.Active, secret);
    }
}

public sealed class CreateWebhookEndpointEndpoint(IDispatcher dispatcher)
    : Endpoint<CreateWebhookEndpointRequest, WebhookEndpointResponse>
{
    public override void Configure()
    {
        Post("/v1/webhook-endpoints");
        Description(x => x.WithTags("Webhooks"));
        Summary(s => s.Summary = "Webhook alıcısı ekler; imza sırrı (whsec_…) yalnız bu yanıtta görünür.");
    }

    public override async Task HandleAsync(CreateWebhookEndpointRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new CreateWebhookEndpointCommand(req.Url, req.EventTypes), ct), ct);
}

public sealed record ListWebhookEndpointsQuery : IQuery<IReadOnlyList<WebhookEndpointResponse>>;

public sealed class ListWebhookEndpointsHandler(WebhooksDbContext db)
    : IQueryHandler<ListWebhookEndpointsQuery, IReadOnlyList<WebhookEndpointResponse>>
{
    public async Task<IReadOnlyList<WebhookEndpointResponse>> Handle(
        ListWebhookEndpointsQuery query, CancellationToken ct)
        => await db.WebhookEndpoints.AsNoTracking()
            .OrderBy(e => e.CreatedAt)
            .Select(e => new WebhookEndpointResponse(e.Id, e.Url, e.EventTypes, e.Active, null))
            .ToListAsync(ct);
}

public sealed class ListWebhookEndpointsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<WebhookEndpointResponse>>
{
    public override void Configure()
    {
        Get("/v1/webhook-endpoints");
        Description(x => x.WithTags("Webhooks"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListWebhookEndpointsQuery(), ct), ct);
}

public sealed record RotateWebhookSecretCommand(Guid EndpointId)
    : Poyra.SharedKernel.Cqrs.ICommand<WebhookEndpointResponse>;

public sealed class RotateWebhookSecretHandler(WebhooksDbContext db, ICredentialProtector protector)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<RotateWebhookSecretCommand, WebhookEndpointResponse>
{
    public async Task<WebhookEndpointResponse> Handle(RotateWebhookSecretCommand command, CancellationToken ct)
    {
        var endpoint = await db.WebhookEndpoints.SingleOrDefaultAsync(e => e.Id == command.EndpointId, ct)
            ?? throw PoyraException.NotFound("webhook_endpoint.not_found", "Webhook alıcısı bulunamadı.");

        var secret = WebhookSecrets.Generate();
        endpoint.SecretEncrypted = protector.Protect(new Dictionary<string, string> { ["secret"] = secret });
        await db.SaveChangesAsync(ct);

        return new WebhookEndpointResponse(endpoint.Id, endpoint.Url, endpoint.EventTypes, endpoint.Active, secret);
    }
}

public sealed class RotateWebhookSecretEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<WebhookEndpointResponse>
{
    public override void Configure()
    {
        Post("/v1/webhook-endpoints/{id}/rotate-secret");
        Description(x => x.WithTags("Webhooks"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new RotateWebhookSecretCommand(Route<Guid>("id")), ct), ct);
}

public sealed record SetWebhookEndpointStatusRequest(bool Active);

public sealed record SetWebhookEndpointStatusCommand(Guid EndpointId, bool Active)
    : Poyra.SharedKernel.Cqrs.ICommand<WebhookEndpointResponse>;

public sealed class SetWebhookEndpointStatusHandler(WebhooksDbContext db)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SetWebhookEndpointStatusCommand, WebhookEndpointResponse>
{
    public async Task<WebhookEndpointResponse> Handle(SetWebhookEndpointStatusCommand command, CancellationToken ct)
    {
        var endpoint = await db.WebhookEndpoints.SingleOrDefaultAsync(e => e.Id == command.EndpointId, ct)
            ?? throw PoyraException.NotFound("webhook_endpoint.not_found", "Webhook alıcısı bulunamadı.");

        endpoint.Active = command.Active;
        await db.SaveChangesAsync(ct);
        return new WebhookEndpointResponse(endpoint.Id, endpoint.Url, endpoint.EventTypes, endpoint.Active, null);
    }
}

public sealed class SetWebhookEndpointStatusEndpoint(IDispatcher dispatcher)
    : Endpoint<SetWebhookEndpointStatusRequest, WebhookEndpointResponse>
{
    public override void Configure()
    {
        Post("/v1/webhook-endpoints/{id}/status");
        Description(x => x.WithTags("Webhooks"));
    }

    public override async Task HandleAsync(SetWebhookEndpointStatusRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new SetWebhookEndpointStatusCommand(Route<Guid>("id"), req.Active), ct), ct);
}
