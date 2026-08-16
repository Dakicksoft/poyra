using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Installments.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Installments.Features;

public sealed record SchemeDto(
    Guid Id, Guid ConnectorAccountId, string Program, int InstallmentCount, int CustomerRateBps, bool Active);

public sealed record UpsertSchemeRequest(
    Guid ConnectorAccountId, string Program, int InstallmentCount, int CustomerRateBps, bool Active = true);

public sealed record UpsertSchemeCommand(
    Guid ConnectorAccountId, string Program, int InstallmentCount, int CustomerRateBps, bool Active)
    : Poyra.SharedKernel.Cqrs.ICommand<SchemeDto>;

public sealed class UpsertSchemeValidator : AbstractValidator<UpsertSchemeCommand>
{
    public UpsertSchemeValidator()
    {
        RuleFor(x => x.Program).NotEmpty().MaximumLength(20);
        RuleFor(x => x.InstallmentCount).InclusiveBetween(2, 12);
        RuleFor(x => x.CustomerRateBps).InclusiveBetween(0, 10_000);
    }
}

public sealed class UpsertSchemeHandler(InstallmentsDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<UpsertSchemeCommand, SchemeDto>
{
    public async Task<SchemeDto> Handle(UpsertSchemeCommand command, CancellationToken ct)
    {
        var program = command.Program.ToLowerInvariant();
        var scheme = await db.InstallmentSchemes.SingleOrDefaultAsync(s =>
            s.ConnectorAccountId == command.ConnectorAccountId
            && s.Program == program
            && s.InstallmentCount == command.InstallmentCount, ct);

        if (scheme is null)
        {
            scheme = new InstallmentScheme
            {
                TenantId = tenant.TenantId,
                ConnectorAccountId = command.ConnectorAccountId,
                Program = program,
                InstallmentCount = command.InstallmentCount,
            };
            db.InstallmentSchemes.Add(scheme);
        }

        scheme.CustomerRateBps = command.CustomerRateBps;
        scheme.Active = command.Active;
        await db.SaveChangesAsync(ct);

        return new SchemeDto(scheme.Id, scheme.ConnectorAccountId, scheme.Program,
            scheme.InstallmentCount, scheme.CustomerRateBps, scheme.Active);
    }
}

public sealed class UpsertSchemeEndpoint(IDispatcher dispatcher) : Endpoint<UpsertSchemeRequest, SchemeDto>
{
    public override void Configure()
    {
        Post("/v1/installments/schemes");
        Description(x => x.WithTags("Installments"));
        Summary(s => s.Summary = "Taksit şeması ekler/günceller: POS hesabı × kart programı × taksit → vade farkı (bps).");
    }

    public override async Task HandleAsync(UpsertSchemeRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new UpsertSchemeCommand(
            req.ConnectorAccountId, req.Program, req.InstallmentCount, req.CustomerRateBps, req.Active), ct), ct);
}

public sealed record ListSchemesQuery : IQuery<IReadOnlyList<SchemeDto>>;

public sealed class ListSchemesHandler(InstallmentsDbContext db)
    : IQueryHandler<ListSchemesQuery, IReadOnlyList<SchemeDto>>
{
    public async Task<IReadOnlyList<SchemeDto>> Handle(ListSchemesQuery query, CancellationToken ct)
        => await db.InstallmentSchemes.AsNoTracking()
            .OrderBy(s => s.ConnectorAccountId).ThenBy(s => s.Program).ThenBy(s => s.InstallmentCount)
            .Select(s => new SchemeDto(s.Id, s.ConnectorAccountId, s.Program,
                s.InstallmentCount, s.CustomerRateBps, s.Active))
            .ToListAsync(ct);
}

public sealed class ListSchemesEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<SchemeDto>>
{
    public override void Configure()
    {
        Get("/v1/installments/schemes");
        Description(x => x.WithTags("Installments"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListSchemesQuery(), ct), ct);
}

public sealed record QuoteOptionDto(
    Guid ConnectorAccountId,
    string AccountLabel,
    int InstallmentCount, // 1 = tek çekim
    int CustomerRateBps,
    long TotalAmountMinor,
    long MonthlyAmountMinor,
    long LastMonthAmountMinor);

public sealed record InstallmentQuoteResponse(
    long AmountMinor,
    string Currency,
    CardBinDto? Bin,
    IReadOnlyList<QuoteOptionDto> Options);

public sealed record QuoteInstallmentsQuery(string Bin, long AmountMinor)
    : IQuery<InstallmentQuoteResponse>;

public sealed class QuoteInstallmentsValidator : AbstractValidator<QuoteInstallmentsQuery>
{
    public QuoteInstallmentsValidator()
    {
        RuleFor(x => x.Bin).Matches("^[0-9]{6,8}$")
            .WithMessage("BIN 6-8 haneli rakam olmalı (kart numarasının tamamını GÖNDERMEYİN).");
        RuleFor(x => x.AmountMinor).GreaterThan(0);
    }
}

public sealed class QuoteInstallmentsHandler(
    InstallmentsDbContext db, IConnectorAccountsDirectory accounts)
    : IQueryHandler<QuoteInstallmentsQuery, InstallmentQuoteResponse>
{
    public async Task<InstallmentQuoteResponse> Handle(QuoteInstallmentsQuery query, CancellationToken ct)
    {
        var activeAccounts = await accounts.GetActiveAccountsAsync(ct);
        if (activeAccounts.Count == 0)
            throw new PoyraException(409, "installments.no_account", "Aktif bağlantı hesabı yok.");

        var binCandidates = new[] { query.Bin, query.Bin[..6] }.Distinct().ToArray();
        var bin = await db.CardBins.AsNoTracking()
            .Where(b => binCandidates.Contains(b.Bin))
            .OrderByDescending(b => b.Bin.Length)
            .FirstOrDefaultAsync(ct);

        var options = new List<QuoteOptionDto>();

        // Tek çekim her hesapta her zaman vardır
        foreach (var account in activeAccounts)
            options.Add(new QuoteOptionDto(account.Id, account.Label, 1, 0,
                query.AmountMinor, query.AmountMinor, query.AmountMinor));

        // Taksit yalnız kredi kartına ve katalogda tanınan BIN'e önerilir
        if (bin is { CardType: "credit" })
        {
            var accountIds = activeAccounts.Select(a => a.Id).ToArray();
            var schemes = await db.InstallmentSchemes.AsNoTracking()
                .Where(s => s.Active
                            && accountIds.Contains(s.ConnectorAccountId)
                            && (s.Program == bin.Program || s.Program == "*"))
                .ToListAsync(ct);

            var labels = activeAccounts.ToDictionary(a => a.Id, a => a.Label);
            foreach (var scheme in schemes
                         .GroupBy(s => (s.ConnectorAccountId, s.InstallmentCount))
                         // aynı hesap+taksit için programa özel şema, "*" şemasını ezer
                         .Select(g => g.OrderByDescending(s => s.Program != "*").First()))
            {
                var total = InstallmentMath.TotalWithRate(query.AmountMinor, scheme.CustomerRateBps);
                var (monthly, lastMonth) = InstallmentMath.MonthlySplit(total, scheme.InstallmentCount);
                options.Add(new QuoteOptionDto(
                    scheme.ConnectorAccountId, labels[scheme.ConnectorAccountId],
                    scheme.InstallmentCount, scheme.CustomerRateBps, total, monthly, lastMonth));
            }
        }

        return new InstallmentQuoteResponse(
            query.AmountMinor, "TRY",
            bin is null ? null : CardBinMap.ToDto(bin),
            options.OrderBy(o => o.ConnectorAccountId).ThenBy(o => o.InstallmentCount).ToList());
    }
}

public sealed record QuoteRequest(string Bin, long AmountMinor);

public sealed class QuoteInstallmentsEndpoint(IDispatcher dispatcher)
    : Endpoint<QuoteRequest, InstallmentQuoteResponse>
{
    public override void Configure()
    {
        Post("/v1/installments/quote");
        Description(x => x.WithTags("Installments"));
        Summary(s => s.Summary = "BIN (ilk 6-8 hane) + tutar → hesap bazında taksit tablosu (vade farkı dahil).");
    }

    public override async Task HandleAsync(QuoteRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new QuoteInstallmentsQuery(req.Bin, req.AmountMinor), ct), ct);
}
