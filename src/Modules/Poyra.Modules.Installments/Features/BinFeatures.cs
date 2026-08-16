using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Installments.Domain;
using Poyra.SharedKernel.Cqrs;

namespace Poyra.Modules.Installments.Features;

public sealed record CardBinDto(
    string Bin, string BankCode, string BankName, string Program,
    string Brand, string CardType, bool IsCommercial);

internal static class CardBinMap
{
    public static CardBinDto ToDto(CardBin bin)
        => new(bin.Bin, bin.BankCode, bin.BankName, bin.Program, bin.Brand, bin.CardType, bin.IsCommercial);
}

public sealed record UpsertCardBinsRequest(List<CardBinDto> Bins);

public sealed record UpsertCardBinsCommand(List<CardBinDto> Bins)
    : Poyra.SharedKernel.Cqrs.ICommand<int>;

public sealed class UpsertCardBinsValidator : AbstractValidator<UpsertCardBinsCommand>
{
    public UpsertCardBinsValidator()
    {
        RuleFor(x => x.Bins).NotEmpty();
        RuleForEach(x => x.Bins).ChildRules(bin =>
        {
            bin.RuleFor(b => b.Bin).Matches("^[0-9]{6,8}$")
                .WithMessage("BIN 6-8 haneli rakam olmalı (PAN girmeyin!).");
            bin.RuleFor(b => b.BankCode).NotEmpty().MaximumLength(8);
            bin.RuleFor(b => b.BankName).NotEmpty().MaximumLength(100);
            bin.RuleFor(b => b.Program).NotEmpty().MaximumLength(20);
            bin.RuleFor(b => b.Brand).NotEmpty().MaximumLength(20);
            bin.RuleFor(b => b.CardType).NotEmpty().MaximumLength(10);
        });
    }
}

public sealed class UpsertCardBinsHandler(InstallmentsDbContext db)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<UpsertCardBinsCommand, int>
{
    public async Task<int> Handle(UpsertCardBinsCommand command, CancellationToken ct)
    {
        var incoming = command.Bins.ToDictionary(b => b.Bin);
        var existing = await db.CardBins
            .Where(b => incoming.Keys.Contains(b.Bin))
            .ToDictionaryAsync(b => b.Bin, ct);

        foreach (var dto in command.Bins)
        {
            if (existing.TryGetValue(dto.Bin, out var bin))
            {
                bin.BankCode = dto.BankCode;
                bin.BankName = dto.BankName;
                bin.Program = dto.Program.ToLowerInvariant();
                bin.Brand = dto.Brand.ToLowerInvariant();
                bin.CardType = dto.CardType.ToLowerInvariant();
                bin.IsCommercial = dto.IsCommercial;
            }
            else
            {
                db.CardBins.Add(new CardBin
                {
                    Bin = dto.Bin,
                    BankCode = dto.BankCode,
                    BankName = dto.BankName,
                    Program = dto.Program.ToLowerInvariant(),
                    Brand = dto.Brand.ToLowerInvariant(),
                    CardType = dto.CardType.ToLowerInvariant(),
                    IsCommercial = dto.IsCommercial,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return command.Bins.Count;
    }
}

public sealed record UpsertCardBinsResponse(int Count);

public sealed class UpsertCardBinsEndpoint(IDispatcher dispatcher)
    : Endpoint<UpsertCardBinsRequest, UpsertCardBinsResponse>
{
    public override void Configure()
    {
        Post("/v1/bins");
        Description(x => x.WithTags("Installments"));
        Summary(s => s.Summary = "BIN kataloğuna toplu yükleme (platform). BIN = ilk 6-8 hane, PAN değildir.");
    }

    public override async Task HandleAsync(UpsertCardBinsRequest req, CancellationToken ct)
        => await Send.OkAsync(new UpsertCardBinsResponse(
            await dispatcher.Send(new UpsertCardBinsCommand(req.Bins), ct)), ct);
}

public sealed record GetBinQuery(string Bin) : IQuery<CardBinDto?>;

public sealed class GetBinHandler(InstallmentsDbContext db) : IQueryHandler<GetBinQuery, CardBinDto?>
{
    public async Task<CardBinDto?> Handle(GetBinQuery query, CancellationToken ct)
    {
        // En uzun eşleşme kazanır: 8 hane varsa 6'lık genel kayda tercih edilir
        var candidates = new[] { query.Bin.PadRight(8)[..8].Trim(), query.Bin[..Math.Min(6, query.Bin.Length)] }
            .Where(c => c.Length >= 6)
            .Distinct()
            .ToArray();

        var bin = await db.CardBins.AsNoTracking()
            .Where(b => candidates.Contains(b.Bin))
            .OrderByDescending(b => b.Bin.Length)
            .FirstOrDefaultAsync(ct);

        return bin is null ? null : CardBinMap.ToDto(bin);
    }
}

public sealed class GetBinEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<CardBinDto>
{
    public override void Configure()
    {
        Get("/v1/bins/{bin}");
        Description(x => x.WithTags("Installments"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await dispatcher.Ask(new GetBinQuery(Route<string>("bin")!), ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(result, ct);
    }
}
