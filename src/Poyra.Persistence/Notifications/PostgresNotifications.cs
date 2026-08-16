using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Poyra.SharedKernel.Notifications;

namespace Poyra.Persistence.Notifications;

/// <summary>
/// Postgres LISTEN/NOTIFY tabanlı anlık bildirim. Redis gibi ek bir bileşen EKLEMEZ.
/// Ölçek gerektirdiğinde  bu iki sınıf Redis backplane ile değiştirilir;
/// arayüzler (SharedKernel) sabit kalır.
/// </summary>
public static class PostgresNotifications
{
    public const string Channel = "poyra_events";
}

public sealed class PostgresNotificationPublisher(
    string connectionString, ILogger<PostgresNotificationPublisher> logger) : INotificationPublisher
{
    public async Task PublishAsync(PoyraNotification notification, CancellationToken ct = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);

            // pg_notify parametreli çağrılır (kanal adı sabit, gövde JSON)
            await using var command = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", connection);
            command.Parameters.AddWithValue("channel", PostgresNotifications.Channel);
            command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(notification));
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Anlık bildirim gönderilemedi: {Type}", notification.Type);
        }
    }
}

public sealed class PostgresNotificationListener(
    string connectionString, ILogger<PostgresNotificationListener> logger)
    : BackgroundService, INotificationStream
{
    public event Action<PoyraNotification>? Received;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                connection.Notification += (_, args) =>
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<PoyraNotification>(args.Payload) is { } notification)
                            Received?.Invoke(notification);
                    }
                    catch (JsonException ex)
                    {
                        logger.LogWarning(ex, "Bildirim gövdesi çözümlenemedi.");
                    }
                };

                await connection.OpenAsync(stoppingToken);
                await using (var command = new NpgsqlCommand($"LISTEN {PostgresNotifications.Channel}", connection))
                {
                    await command.ExecuteNonQueryAsync(stoppingToken);
                }

                delay = TimeSpan.FromSeconds(1);
                logger.LogInformation("Canlı bildirim kanalı dinleniyor: {Channel}",
                    PostgresNotifications.Channel);

                while (!stoppingToken.IsCancellationRequested)
                    await connection.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bildirim kanalı koptu, {Delay} sn sonra yeniden denenecek.",
                    delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }
}
