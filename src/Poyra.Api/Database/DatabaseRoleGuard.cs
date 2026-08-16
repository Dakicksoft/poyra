using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Poyra.Api.Database;

public static class DatabaseRoleGuard
{
    public static async Task EnsureNotPrivilegedAsync(
        string connectionString,
        IHostEnvironment environment,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        string user;
        bool superUser, bypassRls;

        try
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
                return;

            user = reader.GetString(0);
            superUser = reader.GetBoolean(1);
            bypassRls = reader.GetBoolean(2);
        }
        catch (NpgsqlException exception)
        {
            logger.LogWarning(exception, "Rol yetkisi doğrulanamadı — veritabanına ulaşılamadı.");
            return;
        }

        if (!superUser && !bypassRls)
        {
            logger.LogInformation(
                "İşyeri yalıtımı B katmanı etkin: uygulama '{User}' rolüyle bağlı (RLS'e tabi).", user);
            return;
        }

        var reason = superUser ? "SUPERUSER" : "BYPASSRLS";
        var message =
            $"Uygulama '{user}' rolüyle bağlanıyor ve bu rol {reason} yetkisine sahip. "
            + "Postgres RLS politikaları bu rol için UYGULANMAZ: işyeri yalıtımının B katmanı "
            + "tamamen devre dışıdır ve tek bir yanlış sorgu tüm işyerlerinin verisini "
            + "döndürebilir. Uygulama 'poyra_app' (NOSUPERUSER, NOBYPASSRLS) rolüyle bağlanmalıdır; "
            + "sahip rol yalnız migration koşar.";

        if (environment.IsProduction())
            throw new InvalidOperationException(message);

        logger.LogWarning("{Problem}\n(Üretimde bu açılışı engellerdi.)", message);
    }
}
