using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Poyra.SharedKernel.Security;

public static class StartupSecrets
{
    /// <summary>Örnek/geliştirme anahtarları — üretimde bunlarla açılmak felakettir.</summary>
    private static readonly string[] KnownWeakKeys =
    [
        Convert.ToBase64String(new byte[32]),                                  // hep sıfır
        Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()),      // test anahtarı
        Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()),      // test anahtarı
        "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=",                        // dev örneği
    ];


    public static IReadOnlyList<string> Validate(
        IConfiguration configuration, IHostEnvironment environment, params string[] required)
    {
        var problems = new List<string>();

        foreach (var name in required)
        {
            var raw = configuration[$"Poyra:{name}"];

            if (string.IsNullOrWhiteSpace(raw))
            {
                problems.Add($"Poyra:{name} tanımlı değil (base64, 32 bayt).");
                continue;
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(raw);
            }
            catch (FormatException)
            {
                problems.Add($"Poyra:{name} geçerli base64 değil.");
                continue;
            }

            if (key.Length != 32)
            {
                problems.Add($"Poyra:{name} 32 bayt olmalı (şu an {key.Length}).");
                continue;
            }

            if (KnownWeakKeys.Contains(raw))
                problems.Add($"Poyra:{name} bilinen bir ÖRNEK anahtar — üretimde kullanılamaz.");
        }

        var appConnection = configuration.GetConnectionString("Poyra");
        if (!string.IsNullOrWhiteSpace(appConnection))
        {
            var user = ConnectionUser(appConnection);
            if (user is not null && !user.Equals("poyra_app", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"ConnectionStrings:Poyra '{user}' rolüyle bağlanıyor. Uygulama YALNIZ "
                    + "RLS'e tabi 'poyra_app' rolüyle bağlanmalıdır (NOBYPASSRLS); sahip ya da "
                    + "süper kullanıcı rolü RLS'i atlar ve İlke 4'ün B katmanı devre dışı kalır.");
            }
        }


        if (environment.IsProduction() && string.IsNullOrWhiteSpace(configuration["Platform:AdminKey"]))
            problems.Add("Platform:AdminKey tanımlı değil — platform uçları kullanılamaz.");

        return problems;
    }

    public static void EnsureOrThrow(
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<string> warn,
        params string[] required)
    {
        var problems = Validate(configuration, environment, required);
        if (problems.Count == 0)
            return;

        var message = "Yapılandırma sorunları:\n  - " + string.Join("\n  - ", problems);

        if (environment.IsProduction())
            throw new InvalidOperationException(
                message + "\n\nÜretimde bu sorunlarla açılmak, hatayı ilk gerçek ödemeye ertelemektir.");

        warn(message + "\n(Üretimde bu açılışı engellerdi.)");
    }


    private static string? ConnectionUser(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;

            var key = part[..separator].Trim().Replace(" ", string.Empty);
            if (key.Equals("Username", StringComparison.OrdinalIgnoreCase)
                || key.Equals("UserID", StringComparison.OrdinalIgnoreCase)
                || key.Equals("UserId", StringComparison.OrdinalIgnoreCase))
            {
                return part[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
