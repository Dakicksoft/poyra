using Microsoft.EntityFrameworkCore.Design;

namespace Poyra.Modules.Payments;

public sealed class PaymentsDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args)
        => PaymentsDbContext.CreateForMigrations(
            Environment.GetEnvironmentVariable("POYRA_MIGRATIONS_CS")
            ?? "Host=localhost;Port=5442;Database=poyra;Username=poyra;Password=poyra_pw");
}
