using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Connectors.Domain;
using Poyra.Modules.Connectors.Infrastructure;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Security;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Connectors.Features;

public sealed record ConnectorAccountResponse(
    Guid Id, string ConnectorKey, string Label, int Priority, bool TestMode, string Status, string Health);

internal static class ConnectorAccountMap
{
    public static ConnectorAccountResponse ToResponse(ConnectorAccount a)
        => new(a.Id, a.ConnectorKey, a.Label, a.Priority, a.TestMode,
            a.Status.ToString().ToLowerInvariant(), a.Health.ToString().ToLowerInvariant());
}

public sealed record AddConnectorAccountRequest(
    string ConnectorKey,
    string Label,
    Dictionary<string, string> Credentials,
    int Priority = 100,
    bool TestMode = true);

public sealed record AddConnectorAccountCommand(
    string ConnectorKey, string Label, Dictionary<string, string> Credentials, int Priority, bool TestMode)
    : Poyra.SharedKernel.Cqrs.ICommand<ConnectorAccountResponse>;

public sealed class AddConnectorAccountValidator : AbstractValidator<AddConnectorAccountCommand>
{
    public AddConnectorAccountValidator()
    {
        RuleFor(x => x.ConnectorKey).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Label).NotEmpty().MinimumLength(2).MaximumLength(100);
        RuleFor(x => x.Priority).InclusiveBetween(1, 1000);
        RuleFor(x => x.Credentials).NotNull();
    }
}

public sealed class AddConnectorAccountHandler(
    ConnectorsDbContext db, TenantContext tenant, ConnectorRegistry registry, ICredentialProtector protector)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<AddConnectorAccountCommand, ConnectorAccountResponse>
{
    public async Task<ConnectorAccountResponse> Handle(AddConnectorAccountCommand command, CancellationToken ct)
    {
        if (!registry.Exists(command.ConnectorKey))
            throw PoyraException.NotFound("connector.unknown", $"Bilinmeyen konnektör: '{command.ConnectorKey}'.");

        // Zorunlu kimlik alanları katalog şemasından denetlenir
        var descriptor = registry.Get(command.ConnectorKey).Descriptor;
        var missing = descriptor.CredentialFields
            .Where(f => f.Required && string.IsNullOrWhiteSpace(command.Credentials.GetValueOrDefault(f.Name)))
            .Select(f => f.Name)
            .ToList();
        if (missing.Count > 0)
            throw new PoyraException(400, "connector_account.missing_credentials",
                $"Eksik kimlik alanları: {string.Join(", ", missing)}");

        if (await db.ConnectorAccounts.AnyAsync(a => a.Label == command.Label, ct))
            throw PoyraException.Conflict("connector_account.label_conflict", $"'{command.Label}' etiketi kullanımda.");

        var account = new ConnectorAccount
        {
            TenantId = tenant.TenantId,
            ConnectorKey = command.ConnectorKey,
            Label = command.Label,
            CredentialsEncrypted = protector.Protect(command.Credentials),
            Priority = command.Priority,
            TestMode = command.TestMode,
        };

        db.ConnectorAccounts.Add(account);
        await db.SaveChangesAsync(ct);

        return ConnectorAccountMap.ToResponse(account);
    }
}

public sealed class AddConnectorAccountEndpoint(IDispatcher dispatcher)
    : Endpoint<AddConnectorAccountRequest, ConnectorAccountResponse>
{
    public override void Configure()
    {
        Post("/v1/connector-accounts");
        Description(x => x.WithTags("Connectors"));
        Summary(s => s.Summary = "Banka/PSP sanal POS hesabı ekler; kimlik bilgileri şifrelenir ve geri okunamaz.");
    }

    public override async Task HandleAsync(AddConnectorAccountRequest req, CancellationToken ct)
    {
        var response = await dispatcher.Send(new AddConnectorAccountCommand(
            req.ConnectorKey, req.Label, req.Credentials, req.Priority, req.TestMode), ct);
        await Send.OkAsync(response, ct);
    }
}

public sealed record ListConnectorAccountsQuery : IQuery<IReadOnlyList<ConnectorAccountResponse>>;

public sealed class ListConnectorAccountsHandler(ConnectorsDbContext db)
    : IQueryHandler<ListConnectorAccountsQuery, IReadOnlyList<ConnectorAccountResponse>>
{
    public async Task<IReadOnlyList<ConnectorAccountResponse>> Handle(ListConnectorAccountsQuery query, CancellationToken ct)
        => await db.ConnectorAccounts.AsNoTracking()
            .OrderBy(a => a.Priority).ThenBy(a => a.CreatedAt)
            .Select(a => ConnectorAccountMap.ToResponse(a))
            .ToListAsync(ct);
}

public sealed class ListConnectorAccountsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<ConnectorAccountResponse>>
{
    public override void Configure()
    {
        Get("/v1/connector-accounts");
        Description(x => x.WithTags("Connectors"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListConnectorAccountsQuery(), ct), ct);
}

public sealed record SetConnectorAccountStatusRequest(string? Status = null, string? Health = null);

public sealed record SetConnectorAccountStatusCommand(Guid AccountId, string? Status, string? Health)
    : Poyra.SharedKernel.Cqrs.ICommand<ConnectorAccountResponse>;

public sealed class SetConnectorAccountStatusHandler(ConnectorsDbContext db)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SetConnectorAccountStatusCommand, ConnectorAccountResponse>
{
    public async Task<ConnectorAccountResponse> Handle(SetConnectorAccountStatusCommand command, CancellationToken ct)
    {
        var account = await db.ConnectorAccounts.SingleOrDefaultAsync(a => a.Id == command.AccountId, ct)
            ?? throw PoyraException.NotFound("connector_account.not_found", "Bağlantı hesabı bulunamadı.");

        if (command.Status is not null)
            account.Status = Enum.Parse<ConnectorAccountStatus>(command.Status, true);
        if (command.Health is not null)
            account.Health = Enum.Parse<ConnectorHealth>(command.Health, true);

        await db.SaveChangesAsync(ct);
        return ConnectorAccountMap.ToResponse(account);
    }
}

public sealed class SetConnectorAccountStatusEndpoint(IDispatcher dispatcher)
    : Endpoint<SetConnectorAccountStatusRequest, ConnectorAccountResponse>
{
    public override void Configure()
    {
        Post("/v1/connector-accounts/{id}/status");
        Description(x => x.WithTags("Connectors"));
        Summary(s => s.Summary = "Hesabı devre dışı bırakır/açar veya sağlık durumunu işaretler (silme yoktur).");
    }

    public override async Task HandleAsync(SetConnectorAccountStatusRequest req, CancellationToken ct)
    {
        var id = Route<Guid>("id");
        var response = await dispatcher.Send(new SetConnectorAccountStatusCommand(id, req.Status, req.Health), ct);
        await Send.OkAsync(response, ct);
    }
}

public sealed record RotateCredentialsRequest(Dictionary<string, string> Credentials);

public sealed record RotateCredentialsCommand(Guid AccountId, Dictionary<string, string> Credentials)
    : Poyra.SharedKernel.Cqrs.ICommand<ConnectorAccountResponse>;

/// <summary>
/// Banka anahtarı süresi dolduğunda veya sızıntı şüphesinde hesap SİLİNMEZ — kimlikler
/// değiştirilir; hesabın kimliği (Id) sabit kalır, geçmiş işlemler ona bağlı durmaya devam eder.
/// Eski kimlikler geri okunamaz (tek yönlü: yalnız üzerine yazılır).
/// </summary>
public sealed class RotateCredentialsHandler(
    ConnectorsDbContext db, ConnectorRegistry registry, ICredentialProtector protector)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<RotateCredentialsCommand, ConnectorAccountResponse>
{
    public async Task<ConnectorAccountResponse> Handle(RotateCredentialsCommand command, CancellationToken ct)
    {
        var account = await db.ConnectorAccounts.SingleOrDefaultAsync(a => a.Id == command.AccountId, ct)
            ?? throw PoyraException.NotFound("connector_account.not_found", "Bağlantı hesabı bulunamadı.");

        var descriptor = registry.Get(account.ConnectorKey).Descriptor;
        var missing = descriptor.CredentialFields
            .Where(f => f.Required && string.IsNullOrWhiteSpace(command.Credentials.GetValueOrDefault(f.Name)))
            .Select(f => f.Name)
            .ToList();
        if (missing.Count > 0)
            throw new PoyraException(400, "connector_account.missing_credentials",
                $"Eksik kimlik alanları: {string.Join(", ", missing)}");

        account.CredentialsEncrypted = protector.Protect(command.Credentials);

        // Yeni kimlikler HEMEN sınanır. Aksi halde yanlış anahtar yazıldığında rota, canary
        // koşana dek (≤10 dk) trafiği bozuk POS'a yollamaya devam ederdi.
        var probe = await ConnectorProbe.RunAsync(registry, protector, account, ct);
        if (probe is not null)
            account.Health = probe.Healthy ? ConnectorHealth.Healthy : ConnectorHealth.Down;

        await db.SaveChangesAsync(ct);

        return ConnectorAccountMap.ToResponse(account);
    }
}

public sealed class RotateCredentialsEndpoint(IDispatcher dispatcher)
    : Endpoint<RotateCredentialsRequest, ConnectorAccountResponse>
{
    public override void Configure()
    {
        Post("/v1/connector-accounts/{id}/credentials");
        Description(x => x.WithTags("Connectors"));
        Summary(s => s.Summary =
            "Hesabın banka kimliklerini değiştirir (anahtar yenileme). Eski değerler geri okunamaz.");
    }

    public override async Task HandleAsync(RotateCredentialsRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new RotateCredentialsCommand(Route<Guid>("id"), req.Credentials), ct), ct);
}

public sealed record UpdateAccountSettingsRequest(string? Label = null, int? Priority = null, bool? TestMode = null);

public sealed record UpdateAccountSettingsCommand(Guid AccountId, string? Label, int? Priority, bool? TestMode)
    : Poyra.SharedKernel.Cqrs.ICommand<ConnectorAccountResponse>;

public sealed class UpdateAccountSettingsValidator : AbstractValidator<UpdateAccountSettingsCommand>
{
    public UpdateAccountSettingsValidator()
    {
        RuleFor(x => x.Label).MinimumLength(2).MaximumLength(100).When(x => x.Label is not null);
        RuleFor(x => x.Priority).InclusiveBetween(1, 1000).When(x => x.Priority.HasValue);
    }
}

public sealed class UpdateAccountSettingsHandler(ConnectorsDbContext db)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<UpdateAccountSettingsCommand, ConnectorAccountResponse>
{
    public async Task<ConnectorAccountResponse> Handle(UpdateAccountSettingsCommand command, CancellationToken ct)
    {
        var account = await db.ConnectorAccounts.SingleOrDefaultAsync(a => a.Id == command.AccountId, ct)
            ?? throw PoyraException.NotFound("connector_account.not_found", "Bağlantı hesabı bulunamadı.");

        if (command.Label is { Length: > 0 } label && label != account.Label)
        {
            if (await db.ConnectorAccounts.AnyAsync(a => a.Label == label && a.Id != account.Id, ct))
                throw PoyraException.Conflict("connector_account.label_conflict", $"'{label}' etiketi kullanımda.");
            account.Label = label;
        }

        if (command.Priority is { } priority)
            account.Priority = priority;
        if (command.TestMode is { } testMode)
            account.TestMode = testMode;

        await db.SaveChangesAsync(ct);
        return ConnectorAccountMap.ToResponse(account);
    }
}

public sealed class UpdateAccountSettingsEndpoint(IDispatcher dispatcher)
    : Endpoint<UpdateAccountSettingsRequest, ConnectorAccountResponse>
{
    public override void Configure()
    {
        Post("/v1/connector-accounts/{id}/settings");
        Description(x => x.WithTags("Connectors"));
        Summary(s => s.Summary = "Etiket, öncelik ve test/canlı modunu günceller.");
    }

    public override async Task HandleAsync(UpdateAccountSettingsRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new UpdateAccountSettingsCommand(
            Route<Guid>("id"), req.Label, req.Priority, req.TestMode), ct), ct);
}

public sealed record ProbeAccountResult(Guid AccountId, string Label, bool Supported, bool Healthy, string? Detail);

public sealed record ProbeAccountCommand(Guid AccountId) : Poyra.SharedKernel.Cqrs.ICommand<ProbeAccountResult>;

/// <summary>
/// Kimlikleri kaydettikten sonra "gerçekten çalışıyor mu?" sorusunun cevabı. Canary 10 dakikada
/// bir koşar; işyeri kurulum anında beklemek zorunda kalmasın diye aynı yoklama elle tetiklenir.
/// </summary>
public sealed class ProbeAccountHandler(
    ConnectorsDbContext db, ConnectorRegistry registry, ICredentialProtector protector)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<ProbeAccountCommand, ProbeAccountResult>
{
    public async Task<ProbeAccountResult> Handle(ProbeAccountCommand command, CancellationToken ct)
    {
        var account = await db.ConnectorAccounts.SingleOrDefaultAsync(a => a.Id == command.AccountId, ct)
            ?? throw PoyraException.NotFound("connector_account.not_found", "Bağlantı hesabı bulunamadı.");

        var probe = await ConnectorProbe.RunAsync(registry, protector, account, ct);

        if (probe is null)
            return new ProbeAccountResult(account.Id, account.Label, false, false,
                "Bu konnektör yoklama desteklemiyor; sağlık işlem sonuçlarından güncellenir.");

        account.Health = probe.Healthy ? ConnectorHealth.Healthy : ConnectorHealth.Down;
        await db.SaveChangesAsync(ct);

        return new ProbeAccountResult(account.Id, account.Label, true, probe.Healthy, probe.Detail);
    }
}

public sealed class ProbeAccountEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<ProbeAccountResult>
{
    public override void Configure()
    {
        Post("/v1/connector-accounts/{id}/probe");
        Description(x => x.WithTags("Connectors"));
        Summary(s => s.Summary = "Hesabı şimdi yoklar ve sağlığını günceller (canary'yi beklemeden).");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new ProbeAccountCommand(Route<Guid>("id")), ct), ct);
}

public sealed record CatalogFieldDto(string Name, string Label, bool Secret, bool Required);

public sealed record CatalogEntryDto(
    string Key, string DisplayName, string Type, bool SupportsInstallments,
    bool SupportsVoid, bool SupportsRefund, IReadOnlyList<CatalogFieldDto> CredentialFields, string? Notes);

public sealed record ListCatalogQuery : IQuery<IReadOnlyList<CatalogEntryDto>>;

public sealed class ListCatalogHandler(ConnectorRegistry registry)
    : IQueryHandler<ListCatalogQuery, IReadOnlyList<CatalogEntryDto>>
{
    public Task<IReadOnlyList<CatalogEntryDto>> Handle(ListCatalogQuery query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CatalogEntryDto>>(registry.Catalog()
            .Select(d => new CatalogEntryDto(
                d.Key, d.DisplayName, d.Type.ToString().ToLowerInvariant(),
                d.SupportsInstallments, d.SupportsVoid, d.SupportsRefund,
                d.CredentialFields.Select(f => new CatalogFieldDto(f.Name, f.Label, f.Secret, f.Required)).ToList(),
                d.Notes))
            .ToList());
}

public sealed class ListCatalogEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<CatalogEntryDto>>
{
    public override void Configure()
    {
        Get("/v1/connectors/catalog");
        Description(x => x.WithTags("Connectors"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListCatalogQuery(), ct), ct);
}
