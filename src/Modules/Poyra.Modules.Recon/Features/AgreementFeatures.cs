using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Recon.Features;

/// <param name="BankCode">Kart bankası kodu — null = genel oran (bkz. CommissionAgreementResolver).</param>
public sealed record AgreementDto(
    Guid Id, Guid ConnectorAccountId, int InstallmentCount, int RateBps, int ValorDays,
    string? BankCode);

public sealed record UpsertAgreementRequest(
    Guid ConnectorAccountId, int InstallmentCount, int RateBps, int ValorDays = 1,
    string? BankCode = null);

public sealed record UpsertAgreementCommand(
    Guid ConnectorAccountId, int InstallmentCount, int RateBps, int ValorDays,
    string? BankCode = null)
    : Poyra.SharedKernel.Cqrs.ICommand<AgreementDto>;

public sealed class UpsertAgreementValidator : AbstractValidator<UpsertAgreementCommand>
{
    public UpsertAgreementValidator()
    {
        RuleFor(x => x.InstallmentCount).InclusiveBetween(1, 12);
        RuleFor(x => x.RateBps).InclusiveBetween(0, 10_000);
        RuleFor(x => x.ValorDays).InclusiveBetween(0, 60);

        // Banka kodu ya yoktur (genel oran) ya da katalogdaki gibi rakam dizisidir.
        // Boş dize kabul edilseydi "genel oran" ile "kodu boş banka" iki ayrı satır olurdu.
        RuleFor(x => x.BankCode)
            .Must(code => code is null || (code.Length is >= 3 and <= 8 && code.All(char.IsAsciiDigit)))
            .WithMessage("Banka kodu 3-8 haneli rakam olmalı ya da hiç verilmemeli (genel oran).");
    }
}

public sealed class UpsertAgreementHandler(ReconDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<UpsertAgreementCommand, AgreementDto>
{
    public async Task<AgreementDto> Handle(UpsertAgreementCommand command, CancellationToken ct)
    {
        var agreement = await db.CommissionAgreements.SingleOrDefaultAsync(a =>
            a.ConnectorAccountId == command.ConnectorAccountId
            && a.InstallmentCount == command.InstallmentCount
            && a.BankCode == command.BankCode, ct);

        if (agreement is null)
        {
            agreement = new Domain.CommissionAgreement
            {
                TenantId = tenant.TenantId,
                ConnectorAccountId = command.ConnectorAccountId,
                InstallmentCount = command.InstallmentCount,
                BankCode = command.BankCode,
            };
            db.CommissionAgreements.Add(agreement);
        }

        agreement.RateBps = command.RateBps;
        agreement.ValorDays = command.ValorDays;
        await db.SaveChangesAsync(ct);

        return new AgreementDto(agreement.Id, agreement.ConnectorAccountId,
            agreement.InstallmentCount, agreement.RateBps, agreement.ValorDays, agreement.BankCode);
    }
}

public sealed class UpsertAgreementEndpoint(IDispatcher dispatcher)
    : Endpoint<UpsertAgreementRequest, AgreementDto>
{
    public override void Configure()
    {
        Post("/v1/recon/agreements");
        Description(x => x.WithTags("Recon"));
        Summary(s => s.Summary =
            "Banka komisyon anlaşması: hesap × taksit (× kart bankası) → oran (bps) + valör günü. "
            + "bankCode verilirse o bankanın kartlarına özel (on-us) oran; verilmezse genel oran.");
    }

    public override async Task HandleAsync(UpsertAgreementRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new UpsertAgreementCommand(
            req.ConnectorAccountId, req.InstallmentCount, req.RateBps, req.ValorDays,
            req.BankCode), ct), ct);
}

public sealed record ListAgreementsQuery : IQuery<IReadOnlyList<AgreementDto>>;

public sealed class ListAgreementsHandler(ReconDbContext db)
    : IQueryHandler<ListAgreementsQuery, IReadOnlyList<AgreementDto>>
{
    public async Task<IReadOnlyList<AgreementDto>> Handle(ListAgreementsQuery query, CancellationToken ct)
        => await db.CommissionAgreements.AsNoTracking()
            .OrderBy(a => a.ConnectorAccountId).ThenBy(a => a.InstallmentCount).ThenBy(a => a.BankCode)
            .Select(a => new AgreementDto(a.Id, a.ConnectorAccountId, a.InstallmentCount, a.RateBps,
                a.ValorDays, a.BankCode))
            .ToListAsync(ct);
}

public sealed class ListAgreementsEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<AgreementDto>>
{
    public override void Configure()
    {
        Get("/v1/recon/agreements");
        Description(x => x.WithTags("Recon"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListAgreementsQuery(), ct), ct);
}
