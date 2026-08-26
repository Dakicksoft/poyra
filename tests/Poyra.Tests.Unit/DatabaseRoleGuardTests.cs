using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Poyra.Persistence;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Guard'ın asıl işi, uygulamanın RLS'i atlayan bir rolle açılmasını engellemek.
/// Ama veritabanına HİÇ bağlanılamadığında da sessiz kalmamalı: kalıcı bir kimlik
/// hatasında uygulama zaten hiçbir iş yapamaz, yalnız 503 servis eder — üstelik
/// yetki doğrulaması da hiç koşmamış olur, yani güvenlik güvencesi sessizce atlanır.
/// </summary>
public sealed class DatabaseRoleGuardTests
{
    private static PostgresException PostgresError(string sqlState, string message = "hata")
        => new(message, "FATAL", "FATAL", sqlState);

    // 28P01 yanlış parola · 28000 rol yok · 3D000 veritabanı yok — üçü de beklemekle geçmez.
    [Theory]
    [InlineData("28P01")]
    [InlineData("28000")]
    [InlineData("3D000")]
    public async Task Kalici_kimlik_hatasi_uretimde_acilisi_durdurmali(string sqlState)
    {
        var error = PostgresError(sqlState, "password authentication failed for user \"poyra_app\"");

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            DatabaseRoleGuard.EnsureNotPrivilegedAsync(
                _ => Task.FromException<DatabaseRoleFacts?>(error),
                HostEnv("Production"), NullLogger.Instance));

        thrown.Message.ShouldContain(sqlState);
    }

    [Fact]
    public async Task Kalici_kimlik_hatasi_gelistirmede_yalnizca_uyarmali()
    {
        var logger = new CapturingLogger();

        await Should.NotThrowAsync(() =>
            DatabaseRoleGuard.EnsureNotPrivilegedAsync(
                _ => Task.FromException<DatabaseRoleFacts?>(PostgresError("28P01")),
                HostEnv("Development"), logger));

        // Geçici bir kopukluktan ayırt edilebilmeli: uyarı, bunun üretimde açılışı
        // engelleyecek KALICI bir hata olduğunu söylemeli.
        logger.Warnings.ShouldContain(m => m.Contains("28P01") && m.Contains("engellerdi"));
    }

    // --- Geçici hatalar: beklemekle geçebilir, kısa bir yeniden deneme hakkı olmalı ---

    [Fact]
    public async Task Gecici_hata_yeniden_denenmeli_ve_basarili_olunca_gecmeli()
    {
        var attempts = 0;

        await DatabaseRoleGuard.EnsureNotPrivilegedAsync(
            _ =>
            {
                attempts++;
                return attempts < 3
                    ? Task.FromException<DatabaseRoleFacts?>(new NpgsqlException("bağlanılamadı"))
                    : Task.FromResult<DatabaseRoleFacts?>(new DatabaseRoleFacts("poyra_app", false, false));
            },
            HostEnv("Production"), NullLogger.Instance,
            attempts: 3, retryDelay: TimeSpan.Zero);

        attempts.ShouldBe(3);
    }

    [Fact]
    public async Task Gecici_hata_surerse_uretimde_bile_acilisi_durdurmamali()
    {
        var attempts = 0;
        var logger = new CapturingLogger();

        await Should.NotThrowAsync(() =>
            DatabaseRoleGuard.EnsureNotPrivilegedAsync(
                _ =>
                {
                    attempts++;
                    return Task.FromException<DatabaseRoleFacts?>(new NpgsqlException("bağlanılamadı"));
                },
                HostEnv("Production"), logger,
                attempts: 3, retryDelay: TimeSpan.Zero));

        attempts.ShouldBe(3);
        logger.Warnings.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Kalici_hata_yeniden_DENENMEMELI()
    {
        var attempts = 0;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            DatabaseRoleGuard.EnsureNotPrivilegedAsync(
                _ =>
                {
                    attempts++;
                    return Task.FromException<DatabaseRoleFacts?>(PostgresError("28P01"));
                },
                HostEnv("Production"), NullLogger.Instance,
                attempts: 3, retryDelay: TimeSpan.Zero));

        // Yanlış parola beklemekle düzelmez; açılışı boş yere geciktirmemeli.
        attempts.ShouldBe(1);
    }

    private static IHostEnvironment HostEnv(string name) => new FakeHostEnvironment { EnvironmentName = name };

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Poyra.Api";
        public string ContentRootPath { get; set; } = "/app";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel is LogLevel.Warning or LogLevel.Error)
                Warnings.Add(formatter(state, exception) + " " + exception);
        }
    }
}
