using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Poyra.Api.Database;

/// <summary>Guard'ın veritabanından okuduğu rol bilgisi.</summary>
public sealed record DatabaseRoleFacts(string User, bool SuperUser, bool BypassRls);

public static class DatabaseRoleGuard
{
    /// <summary>
    /// Yeniden denemekle GEÇMEYEN bağlantı hataları: yanlış parola, olmayan rol,
    /// olmayan veritabanı. Geçici kopukluklardan (ağ, henüz açılmamış sunucu) ayrılırlar
    /// çünkü beklemek bunları düzeltmez — yalnız açılışı geciktirir.
    /// </summary>
    private static readonly string[] KaliciHatalar =
    [
        "28P01",   // invalid_password
        "28000",   // invalid_authorization_specification (rol yok)
        "3D000",   // invalid_catalog_name (veritabanı yok)
    ];

    public static Task EnsureNotPrivilegedAsync(
        string connectionString,
        IHostEnvironment environment,
        ILogger logger,
        CancellationToken cancellationToken = default)
        => EnsureNotPrivilegedAsync(
            token => ProbeAsync(connectionString, token),
            environment, logger, cancellationToken: cancellationToken);

    /// <summary>Yoklamayı dışarıdan alan aşırı yükleme — testler gerçek veritabanı olmadan koşar.</summary>
    public static async Task EnsureNotPrivilegedAsync(
        Func<CancellationToken, Task<DatabaseRoleFacts?>> probe,
        IHostEnvironment environment,
        ILogger logger,
        int denemeSayisi = 3,
        TimeSpan? denemeAraligi = null,
        CancellationToken cancellationToken = default)
    {
        var aralik = denemeAraligi ?? TimeSpan.FromSeconds(2);

        DatabaseRoleFacts? facts = null;
        NpgsqlException? gecici = null;

        for (var deneme = 1; deneme <= denemeSayisi; deneme++)
        {
            try
            {
                facts = await probe(cancellationToken);
                gecici = null;
                break;
            }
            catch (PostgresException exception) when (KaliciHatalar.Contains(exception.SqlState))
            {
                // Bu hata beklemekle geçmez, o yüzden yeniden denenmez. Uygulama bu haldeyken
                // hiçbir iş yapamaz — yalnız 503 servis eder — ve rol yetkisi doğrulaması hiç
                // koşmamış olur, yani İlke 4'ün B katmanı sessizce atlanır. Üretimde açılışı
                // durdurmak, sağlıksız bir kopyayı yük dengeleyiciye vermekten iyidir.
                var problem =
                    $"Veritabanına bağlanılamıyor ve bu hata beklemekle GEÇMEZ (SqlState {exception.SqlState}): "
                    + $"{exception.MessageText}. Uygulama bu haldeyken yalnız 503 döndürür; ayrıca işyeri "
                    + "yalıtımının B katmanı (rol yetkisi doğrulaması) hiç koşmamış olur.";

                if (environment.IsProduction())
                    throw new InvalidOperationException(problem, exception);

                logger.LogWarning(exception, "{Problem}\n(Üretimde bu açılışı engellerdi.)", problem);
                return;
            }
            catch (NpgsqlException exception)
            {
                // Geçici olabilir: postgres henüz açılmamış, ağ takılmış. Kısa bir hak tanınır.
                gecici = exception;
                if (deneme < denemeSayisi)
                    await Task.Delay(aralik, cancellationToken);
            }
        }

        if (gecici is not null)
        {
            logger.LogWarning(gecici,
                "Rol yetkisi doğrulanamadı — veritabanına {Deneme} denemede ulaşılamadı.", denemeSayisi);
            return;
        }

        if (facts is null)
            return;

        if (!facts.SuperUser && !facts.BypassRls)
        {
            logger.LogInformation(
                "İşyeri yalıtımı B katmanı etkin: uygulama '{User}' rolüyle bağlı (RLS'e tabi).", facts.User);
            return;
        }

        var reason = facts.SuperUser ? "SUPERUSER" : "BYPASSRLS";
        var message =
            $"Uygulama '{facts.User}' rolüyle bağlanıyor ve bu rol {reason} yetkisine sahip. "
            + "Postgres RLS politikaları bu rol için UYGULANMAZ: işyeri yalıtımının B katmanı "
            + "tamamen devre dışıdır ve tek bir yanlış sorgu tüm işyerlerinin verisini "
            + "döndürebilir. Uygulama 'poyra_app' (NOSUPERUSER, NOBYPASSRLS) rolüyle bağlanmalıdır; "
            + "sahip rol yalnız migration koşar.";

        if (environment.IsProduction())
            throw new InvalidOperationException(message);

        logger.LogWarning("{Problem}\n(Üretimde bu açılışı engellerdi.)", message);
    }

    private static async Task<DatabaseRoleFacts?> ProbeAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            SELECT current_user, rolsuper, rolbypassrls
            FROM pg_roles WHERE rolname = current_user
            """, connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new DatabaseRoleFacts(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2));
    }
}
