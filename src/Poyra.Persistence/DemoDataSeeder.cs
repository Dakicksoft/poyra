using Microsoft.Extensions.Logging;

namespace Poyra.Persistence;

/// <summary>
/// Demo verisinin KURULUP kurulmayacağına karar verir; satırları kendisi yazmaz
/// (onu DemoDataWriter yapar). Bu ayrım sayesinde kararlar veritabanı olmadan sınanır.
/// </summary>
public static class DemoDataSeeder
{
    public static async Task<DemoSeedOutcome> SeedAsync(
        DemoSeedOptions options,
        Func<CancellationToken, Task<bool>> tenantExists,
        Func<CancellationToken, Task> seed,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
            return DemoSeedOutcome.Disabled;

        if (string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrWhiteSpace(options.Password))
        {
            logger.LogWarning(
                "Demo tohumlaması açık ama {Section}:Email / :Password verilmemiş — atlanıyor.",
                DemoSeedOptions.Section);
            return DemoSeedOutcome.MissingSettings;
        }

        try
        {
            // Bir tane bile işyeri varsa burası gerçek bir kurulumdur: dokunulmaz.
            if (await tenantExists(cancellationToken))
            {
                logger.LogInformation("Demo tohumlaması atlandı: veritabanında zaten işyeri var.");
                return DemoSeedOutcome.TenantExists;
            }

            await seed(cancellationToken);
            logger.LogInformation("Demo verisi kuruldu (giriş: {Email}).", options.Email);
            return DemoSeedOutcome.Seeded;
        }
        catch (Exception exception)
        {
            // Demo verisi açılışı düşürmeye değmez: uygulama demo verisi olmadan da çalışır.
            logger.LogWarning(exception, "Demo tohumlaması başarısız — açılış sürdürülüyor.");
            return DemoSeedOutcome.Failed;
        }
    }
}
