using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Recon.Domain;
using Poyra.SharedKernel.Cqrs;

namespace Poyra.Modules.Recon.Features;

public sealed record HolidayDto(DateOnly Day, string Name);

public sealed record UpsertHolidaysRequest(List<HolidayDto> Holidays);

public sealed record UpsertHolidaysCommand(List<HolidayDto> Holidays)
    : Poyra.SharedKernel.Cqrs.ICommand<int>;

public sealed class UpsertHolidaysValidator : AbstractValidator<UpsertHolidaysCommand>
{
    public UpsertHolidaysValidator()
    {
        RuleFor(x => x.Holidays).NotEmpty();
        RuleForEach(x => x.Holidays).ChildRules(h => h.RuleFor(x => x.Name).NotEmpty().MaximumLength(100));
    }
}

public sealed class UpsertHolidaysHandler(ReconDbContext db)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<UpsertHolidaysCommand, int>
{
    public async Task<int> Handle(UpsertHolidaysCommand command, CancellationToken ct)
    {
        var days = command.Holidays.Select(h => h.Day).ToArray();
        var existing = await db.BankHolidays.Where(h => days.Contains(h.Day)).ToDictionaryAsync(h => h.Day, ct);

        foreach (var dto in command.Holidays)
        {
            if (existing.TryGetValue(dto.Day, out var holiday))
                holiday.Name = dto.Name;
            else
                db.BankHolidays.Add(new BankHoliday { Day = dto.Day, Name = dto.Name });
        }

        await db.SaveChangesAsync(ct);
        return command.Holidays.Count;
    }
}

public sealed record UpsertHolidaysResponse(int Count);

public sealed class UpsertHolidaysEndpoint(IDispatcher dispatcher)
    : Endpoint<UpsertHolidaysRequest, UpsertHolidaysResponse>
{
    public override void Configure()
    {
        Post("/v1/bank-holidays");
        Description(x => x.WithTags("Recon"));
        Summary(s => s.Summary = "Banka tatili kataloğuna toplu yükleme (platform). İş günü valör hesabı bunu kullanır.");
    }

    public override async Task HandleAsync(UpsertHolidaysRequest req, CancellationToken ct)
        => await Send.OkAsync(new UpsertHolidaysResponse(
            await dispatcher.Send(new UpsertHolidaysCommand(req.Holidays), ct)), ct);
}

public sealed record ListHolidaysQuery(int Year) : IQuery<IReadOnlyList<HolidayDto>>;

public sealed class ListHolidaysHandler(ReconDbContext db)
    : IQueryHandler<ListHolidaysQuery, IReadOnlyList<HolidayDto>>
{
    public async Task<IReadOnlyList<HolidayDto>> Handle(ListHolidaysQuery query, CancellationToken ct)
        => await db.BankHolidays.AsNoTracking()
            .Where(h => h.Day.Year == query.Year)
            .OrderBy(h => h.Day)
            .Select(h => new HolidayDto(h.Day, h.Name))
            .ToListAsync(ct);
}

public sealed class ListHolidaysEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<IReadOnlyList<HolidayDto>>
{
    public override void Configure()
    {
        Get("/v1/bank-holidays");
        Description(x => x.WithTags("Recon"));
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListHolidaysQuery(
            Query<int?>("year", isRequired: false) ?? DateTime.UtcNow.Year), ct), ct);
}
