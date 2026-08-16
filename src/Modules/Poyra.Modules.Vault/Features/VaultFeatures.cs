using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Vault.Domain;
using Poyra.Modules.Vault.Infrastructure;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Vault.Features;

public sealed record CardTokenDto(
    string Token, string MaskedPan, string Brand, int ExpiryMonth, int ExpiryYear,
    string? CustomerRef, DateTimeOffset CreatedAt);

public sealed record TokenizeCardRequest(
    string CardNumber, int ExpiryMonth, int ExpiryYear, string? HolderName = null, string? CustomerRef = null);

public sealed record TokenizeCardCommand(
    string CardNumber, int ExpiryMonth, int ExpiryYear, string? HolderName, string? CustomerRef)
    : Poyra.SharedKernel.Cqrs.ICommand<CardTokenDto>;

public sealed class TokenizeCardValidator : AbstractValidator<TokenizeCardCommand>
{
    public TokenizeCardValidator()
    {
        RuleFor(x => x.CardNumber).NotEmpty()
            .Must(CardNumbers.IsValidLuhn).WithMessage("Kart numarası geçersiz (Luhn).");
        RuleFor(x => x.ExpiryMonth).InclusiveBetween(1, 12);
        RuleFor(x => x.ExpiryYear).InclusiveBetween(2000, 2099);
        RuleFor(x => x.HolderName).MaximumLength(100);
        RuleFor(x => x.CustomerRef).MaximumLength(100);
    }
}

public sealed class TokenizeCardHandler(
    VaultDbContext db, TenantContext tenant, VaultCrypto crypto, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<TokenizeCardCommand, CardTokenDto>
{
    public async Task<CardTokenDto> Handle(TokenizeCardCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var expiryEnd = new DateTimeOffset(
            command.ExpiryYear, command.ExpiryMonth, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        if (expiryEnd <= now)
            throw new PoyraException(400, "vault.card_expired", "Kartın son kullanma tarihi geçmiş.");

        var pan = command.CardNumber.Replace(" ", "").Replace("-", "");
        var fingerprint = crypto.Fingerprint(pan);

        // Aynı müşteri + aynı kart → var olan token döner (çift kayıt yok)
        var existing = await db.CardTokens.AsNoTracking().SingleOrDefaultAsync(
            t => t.CustomerRef == command.CustomerRef && t.Fingerprint == fingerprint && t.DeletedAt == null, ct);
        if (existing is not null)
            return ToDto(existing);

        var card = new CardData(pan, command.ExpiryMonth, command.ExpiryYear, command.HolderName, Cvv: null);
        var token = new CardToken
        {
            TenantId = tenant.TenantId,
            CustomerRef = command.CustomerRef,
            CardEncrypted = crypto.Protect(card),
            Fingerprint = fingerprint,
            MaskedPan = CardNumbers.Mask(pan),
            Brand = CardNumbers.DetectBrand(pan),
            ExpiryMonth = command.ExpiryMonth,
            ExpiryYear = command.ExpiryYear,
        };

        db.CardTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return ToDto(token);
    }

    internal static CardTokenDto ToDto(CardToken t)
        => new(t.PublicToken, t.MaskedPan, t.Brand, t.ExpiryMonth, t.ExpiryYear, t.CustomerRef, t.CreatedAt);
}

public sealed class TokenizeCardEndpoint(IDispatcher dispatcher) : Endpoint<TokenizeCardRequest, CardTokenDto>
{
    public override void Configure()
    {
        Post("/v1/vault/cards");
        Description(x => x.WithTags("Vault"));
        Summary(s => s.Summary = "Kartı saklar ve token (tok_…) döner. CVV saklanmaz; yanıtta PAN yoktur.");
    }

    public override async Task HandleAsync(TokenizeCardRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new TokenizeCardCommand(
            req.CardNumber, req.ExpiryMonth, req.ExpiryYear, req.HolderName, req.CustomerRef), ct), ct);
}

public sealed record ListCardsQuery(string? CustomerRef) : IQuery<IReadOnlyList<CardTokenDto>>;

public sealed class ListCardsHandler(VaultDbContext db)
    : IQueryHandler<ListCardsQuery, IReadOnlyList<CardTokenDto>>
{
    public async Task<IReadOnlyList<CardTokenDto>> Handle(ListCardsQuery query, CancellationToken ct)
    {
        var cards = db.CardTokens.AsNoTracking().Where(t => t.DeletedAt == null);
        if (query.CustomerRef is { } customerRef)
            cards = cards.Where(t => t.CustomerRef == customerRef);

        return await cards.OrderByDescending(t => t.CreatedAt)
            .Select(t => TokenizeCardHandler.ToDto(t))
            .ToListAsync(ct);
    }
}

public sealed class ListCardsEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<IReadOnlyList<CardTokenDto>>
{
    public override void Configure()
    {
        Get("/v1/vault/cards");
        Description(x => x.WithTags("Vault"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(
            new ListCardsQuery(Query<string?>("customerRef", isRequired: false)), ct), ct);
}

public sealed record DeleteCardCommand(string Token) : Poyra.SharedKernel.Cqrs.ICommand<bool>;

public sealed class DeleteCardHandler(VaultDbContext db, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<DeleteCardCommand, bool>
{
    public async Task<bool> Handle(DeleteCardCommand command, CancellationToken ct)
    {
        var token = await db.CardTokens.SingleOrDefaultAsync(
            t => t.PublicToken == command.Token && t.DeletedAt == null, ct);
        if (token is null)
            return false;

        token.DeletedAt = clock.UtcNow;
        db.Entry(token).Property(t => t.CardEncrypted).CurrentValue = [];
        await db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed record DeleteCardResponse(bool Deleted);

public sealed class DeleteCardEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<DeleteCardResponse>
{
    public override void Configure()
    {
        Delete("/v1/vault/cards/{token}");
        Description(x => x.WithTags("Vault"));
        Summary(s => s.Summary = "Kartı kullanımdan kaldırır (kriptografik imha; kayıt izi kalır).");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var deleted = await dispatcher.Send(new DeleteCardCommand(Route<string>("token")!), ct);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new DeleteCardResponse(true), ct);
    }
}
