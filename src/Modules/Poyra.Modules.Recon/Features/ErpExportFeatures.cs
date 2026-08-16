using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Recon.Domain;
using Poyra.Modules.Recon.Infrastructure;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Modules.Recon.Features;

public sealed record ErpSettingsDto(
    string Format,
    string PosReceivableAccount,
    string BankAccount,
    string CommissionExpenseAccount,
    string DocumentPrefix);

public sealed record GetErpSettingsQuery : IQuery<ErpSettingsDto>;

public sealed class GetErpSettingsHandler(ReconDbContext db, TenantContext tenant)
    : IQueryHandler<GetErpSettingsQuery, ErpSettingsDto>
{
    public async Task<ErpSettingsDto> Handle(GetErpSettingsQuery query, CancellationToken ct)
    {
        var settings = await db.ErpExportSettings.AsNoTracking().SingleOrDefaultAsync(ct)
                       ?? ErpExportSettings.Default(tenant.TenantId);

        return Map(settings);
    }

    internal static ErpSettingsDto Map(ErpExportSettings s)
        => new(ErpFormatMap.ToDb[s.Format], s.PosReceivableAccount, s.BankAccount,
            s.CommissionExpenseAccount, s.DocumentPrefix);
}

public sealed class GetErpSettingsEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<ErpSettingsDto>
{
    public override void Configure()
    {
        Get("/v1/recon/erp-settings");
        Description(x => x.WithTags("Recon"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new GetErpSettingsQuery(), ct), ct);
}

public sealed record SaveErpSettingsRequest(
    string Format = "poyra_csv",
    string PosReceivableAccount = "108.01",
    string BankAccount = "102.01",
    string CommissionExpenseAccount = "653.01",
    string DocumentPrefix = "POS");

public sealed record SaveErpSettingsCommand(
    string Format, string PosReceivableAccount, string BankAccount,
    string CommissionExpenseAccount, string DocumentPrefix)
    : Poyra.SharedKernel.Cqrs.ICommand<ErpSettingsDto>;

public sealed class SaveErpSettingsValidator : AbstractValidator<SaveErpSettingsCommand>
{
    public SaveErpSettingsValidator()
    {
        RuleFor(x => x.Format).Must(ErpFormatMap.FromDb.ContainsKey)
            .WithMessage("Geçersiz biçim. Seçenekler: " + string.Join(", ", ErpFormatMap.FromDb.Keys));
        RuleFor(x => x.PosReceivableAccount).Must(BeAccountCode).WithMessage(AccountMessage);
        RuleFor(x => x.BankAccount).Must(BeAccountCode).WithMessage(AccountMessage);
        RuleFor(x => x.CommissionExpenseAccount).Must(BeAccountCode).WithMessage(AccountMessage);
        RuleFor(x => x.DocumentPrefix).NotEmpty().MaximumLength(10)
            .Matches("^[A-Za-z0-9_-]+$")
            .WithMessage("Fiş öneki yalnız harf, rakam, tire ve alt çizgi içerebilir "
                         + "(ayraç karakteri CSV sütunlarını kaydırır).");
    }

    private const string AccountMessage = "Hesap kodu tekdüzen plan biçiminde olmalı (ör. 108.01).";

    private static bool BeAccountCode(string? code)
        => code is { Length: > 0 } && System.Text.RegularExpressions.Regex.IsMatch(
            code, "^[0-9]{3}(\\.[0-9]{1,4})*$");
}

public sealed class SaveErpSettingsHandler(ReconDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SaveErpSettingsCommand, ErpSettingsDto>
{
    public async Task<ErpSettingsDto> Handle(SaveErpSettingsCommand command, CancellationToken ct)
    {
        var settings = await db.ErpExportSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = ErpExportSettings.Default(tenant.TenantId);
            db.ErpExportSettings.Add(settings);
        }

        settings.Format = ErpFormatMap.FromDb[command.Format];
        settings.PosReceivableAccount = command.PosReceivableAccount;
        settings.BankAccount = command.BankAccount;
        settings.CommissionExpenseAccount = command.CommissionExpenseAccount;
        settings.DocumentPrefix = command.DocumentPrefix;

        await db.SaveChangesAsync(ct);
        return GetErpSettingsHandler.Map(settings);
    }
}

public sealed class SaveErpSettingsEndpoint(IDispatcher dispatcher)
    : Endpoint<SaveErpSettingsRequest, ErpSettingsDto>
{
    public override void Configure()
    {
        Post("/v1/recon/erp-settings");
        Description(x => x.WithTags("Recon"));
        Summary(s => s.Summary = "ERP dışa aktarım biçimi ve muhasebe hesap kodlarını ayarlar.");
    }

    public override async Task HandleAsync(SaveErpSettingsRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new SaveErpSettingsCommand(
            req.Format, req.PosReceivableAccount, req.BankAccount,
            req.CommissionExpenseAccount, req.DocumentPrefix), ct), ct);
}

public sealed record ErpExportResult(string FileName, string ContentType, string Content);

public sealed record ExportStatementQuery(Guid StatementId, string? Format) : IQuery<ErpExportResult>;

public sealed class ExportStatementHandler(ReconDbContext db, TenantContext tenant)
    : IQueryHandler<ExportStatementQuery, ErpExportResult>
{
    public async Task<ErpExportResult> Handle(ExportStatementQuery query, CancellationToken ct)
    {
        var statement = await db.ReconStatements.AsNoTracking()
                            .SingleOrDefaultAsync(s => s.Id == query.StatementId, ct)
                        ?? throw PoyraException.NotFound("recon.statement_not_found", "Ekstre bulunamadı.");

        if (statement.Status is not StatementStatus.Matched)
            throw new PoyraException(409, "recon.not_matched",
                $"Ekstre '{statement.Status.ToString().ToLowerInvariant()}' durumunda — "
                + "eşleştirme tamamlanmadan ERP fişi üretilmez.");

        var settings = await db.ErpExportSettings.AsNoTracking().SingleOrDefaultAsync(ct)
                       ?? ErpExportSettings.Default(tenant.TenantId);

        var format = query.Format is { Length: > 0 } requested
            ? ErpFormatMap.FromDb.TryGetValue(requested, out var parsed)
                ? parsed
                : throw new PoyraException(400, "recon.invalid_format",
                    "Geçersiz biçim. Seçenekler: " + string.Join(", ", ErpFormatMap.FromDb.Keys))
            : settings.Format;

        var lines = await db.ReconStatementLines.AsNoTracking()
            .Where(l => l.StatementId == statement.Id)
            .OrderBy(l => l.LineNo)
            .ToListAsync(ct);

        var voucher = ErpVoucherBuilder.Build(settings, statement.StatementDate, "TRY", lines);

        if (!voucher.IsBalanced)
            throw new PoyraException(409, "recon.voucher_unbalanced",
                $"Fiş dengesiz (borç {voucher.DebitTotal / 100m:N2} ≠ alacak {voucher.CreditTotal / 100m:N2}). "
                + "Ekstrede brüt = net + komisyon eşitliği bozuk; satırları kontrol edin.");

        return new ErpExportResult(
            ErpVoucherWriter.FileName(voucher, format),
            "text/csv; charset=utf-8",
            ErpVoucherWriter.Write(voucher, format));
    }
}

public sealed class ExportStatementEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/v1/recon/statements/{id}/erp-export");
        Description(x => x.WithTags("Recon"));
        Summary(s => s.Summary =
            "Gün sonu ekstresinden muhasebe fişi üretir (poyra_csv | logo_csv | mikro_csv).");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await dispatcher.Ask(
            new ExportStatementQuery(Route<Guid>("id"), Query<string>("format", isRequired: false)), ct);

        // UTF-8 BOM: Excel BOM'suz dosyada Türkçe karakterleri bozar ve muhasebeci
        // "ş" yerine "Å" görür — dosya teknik olarak doğru ama kullanılamaz olur.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes(result.Content)).ToArray();

        await Send.BytesAsync(bytes, result.FileName, result.ContentType, cancellation: ct);
    }
}
