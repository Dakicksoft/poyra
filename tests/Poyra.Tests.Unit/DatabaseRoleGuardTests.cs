using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Poyra.Api.Database;
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
    private static PostgresException PgHatasi(string sqlState, string mesaj = "hata")
        => new(mesaj, "FATAL", "FATAL", sqlState);

    // 28P01 yanlış parola · 28000 rol yok · 3D000 veritabanı yok — üçü de beklemekle geçmez.
    [Theory]
    [InlineData("28P01")]
    [InlineData("28000")]
    [InlineData("3D000")]
    public async Task Kalici_kimlik_hatasi_uretimde_acilisi_durdurmali(string sqlState)
    {
        var hata = PgHatasi(sqlState, "password authentication failed for user \"poyra_app\"");

        var atilan = await Should.ThrowAsync<InvalidOperationException>(() =>
            DatabaseRoleGuard.EnsureNotPrivilegedAsync(
                _ => Task.FromException<DatabaseRoleFacts?>(hata),
                Ortam("Production"), NullLogger.Instance));

        atilan.Message.ShouldContain(sqlState);
    }

    [Fact]
    public async Task Kalici_kimlik_hatasi_gelistirmede_yalnizca_uyarmali()
    {
        var gunluk = new YakalayanGunluk();

        await Should.NotThrowAsync(() =>
            DatabaseRoleGuard.EnsureNotPrivilegedAsync(
                _ => Task.FromException<DatabaseRoleFacts?>(PgHatasi("28P01")),
                Ortam("Development"), gunluk));

        // Geçici bir kopukluktan ayırt edilebilmeli: uyarı, bunun üretimde açılışı
        // engelleyecek KALICI bir hata olduğunu söylemeli.
        gunluk.Uyarilar.ShouldContain(m => m.Contains("28P01") && m.Contains("engellerdi"));
    }

    // --- Geçici hatalar: beklemekle geçebilir, kısa bir yeniden deneme hakkı olmalı ---

    [Fact]
    public async Task Gecici_hata_yeniden_denenmeli_ve_basarili_olunca_gecmeli()
    {
        var deneme = 0;

        await DatabaseRoleGuard.EnsureNotPrivilegedAsync(
            _ =>
            {
                deneme++;
                return deneme < 3
                    ? Task.FromException<DatabaseRoleFacts?>(new NpgsqlException("bağlanılamadı"))
                    : Task.FromResult<DatabaseRoleFacts?>(new DatabaseRoleFacts("poyra_app", false, false));
            },
            Ortam("Production"), NullLogger.Instance,
            denemeSayisi: 3, denemeAraligi: TimeSpan.Zero);

        deneme.ShouldBe(3);
    }

    [Fact]
    public async Task Gecici_hata_surerse_uretimde_bile_acilisi_durdurmamali()
    {
        var deneme = 0;
        var gunluk = new YakalayanGunluk();

        await Should.NotThrowAsync(() =>
            DatabaseRoleGuard.EnsureNotPrivilegedAsync(
                _ =>
                {
                    deneme++;
                    return Task.FromException<DatabaseRoleFacts?>(new NpgsqlException("bağlanılamadı"));
                },
                Ortam("Production"), gunluk,
                denemeSayisi: 3, denemeAraligi: TimeSpan.Zero));

        deneme.ShouldBe(3);
        gunluk.Uyarilar.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Kalici_hata_yeniden_DENENMEMELI()
    {
        var deneme = 0;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            DatabaseRoleGuard.EnsureNotPrivilegedAsync(
                _ =>
                {
                    deneme++;
                    return Task.FromException<DatabaseRoleFacts?>(PgHatasi("28P01"));
                },
                Ortam("Production"), NullLogger.Instance,
                denemeSayisi: 3, denemeAraligi: TimeSpan.Zero));

        // Yanlış parola beklemekle düzelmez; açılışı boş yere geciktirmemeli.
        deneme.ShouldBe(1);
    }

    private static IHostEnvironment Ortam(string ad) => new SahteOrtam { EnvironmentName = ad };

    private sealed class SahteOrtam : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Poyra.Api";
        public string ContentRootPath { get; set; } = "/app";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class YakalayanGunluk : ILogger
    {
        public List<string> Uyarilar { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel is LogLevel.Warning or LogLevel.Error)
                Uyarilar.Add(formatter(state, exception) + " " + exception);
        }
    }
}
