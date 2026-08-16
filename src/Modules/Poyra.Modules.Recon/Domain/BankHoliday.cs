using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Recon.Domain;

public sealed class BankHoliday : IHasCreatedAt
{
    public DateOnly Day { get; init; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
