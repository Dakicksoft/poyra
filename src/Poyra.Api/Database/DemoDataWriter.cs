using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Poyra.Modules.Customers;
using Poyra.Modules.Customers.Domain;
using Poyra.Modules.Payments;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Tenancy.Features.CreateTenant;
using Poyra.Persistence;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Time;

namespace Poyra.Api.Database;

/// <summary>
/// Demo satırlarını yazar. Hiçbir metodu veri SİLMEZ; tohumlayıcı zaten yalnız
/// işyeri bulunmayan bir veritabanında çağırır.
/// </summary>
public static class DemoDataWriter
{
    /// <summary>
    /// tenants RLS'siz bir platform tablosudur; işyeri bağlamı kurulmadan sorgulanır
    /// (CreateTenantHandler da slug çakışmasını aynı şekilde kontrol ediyor).
    /// </summary>
    public static async Task<bool> TenantExistsAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TenancyDbContext>();
        return await db.Tenants.AnyAsync(cancellationToken);
    }

    public static async Task WriteAsync(
        IServiceProvider services,
        DemoSeedOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var dispatcher = services.GetRequiredService<IDispatcher>();

        // Mevcut ve doğrulanmış yol: organizasyon + işyeri + varsayılan profil +
        // API anahtarı + parolası hash'lenmiş sahip kullanıcı birlikte kurulur.
        // Elle User üretip parola hash'lemek bu yolu atlamak olurdu.
        var tenant = await dispatcher.Send(
            new CreateTenantCommand(
                options.TenantName,
                options.TenantSlug,
                options.Email,
                options.Password,
                options.OwnerName),
            cancellationToken);

        logger.LogInformation(
            "Demo işyeri kuruldu: {Slug} ({TenantId}).", tenant.Slug, tenant.TenantId);

        // CreateTenantHandler işyeri bağlamını kendisi kurdu; buradan sonraki modül
        // yazmaları RLS altında o işyerine düşer.
        await WriteCustomersAndPaymentsAsync(
            services, tenant.TenantId, tenant.ProfileId, cancellationToken);

        logger.LogInformation("Demo müşteri ve ödeme verisi yazıldı.");
    }

    /// <summary>
    /// Demo müşterileri ve son 30 güne yayılmış ödemeler. Tutarlar ve tarihler
    /// SABİTTİR (Random yok): demo ekran görüntüleri dağıtımlar arasında değişmesin.
    /// </summary>
    private static async Task WriteCustomersAndPaymentsAsync(
        IServiceProvider services, Guid tenantId, Guid profileId, CancellationToken cancellationToken)
    {
        var customersDb = services.GetRequiredService<CustomersDbContext>();
        var paymentsDb = services.GetRequiredService<PaymentsDbContext>();
        var today = services.GetRequiredService<IClock>().UtcNow;

        string[,] people =
        {
            { "mus-001", "Ayşe Yılmaz",  "ayse@ornek.test",   "+905321112233" },
            { "mus-002", "Mehmet Demir", "mehmet@ornek.test", "+905332223344" },
            { "mus-003", "Zeynep Kaya",  "zeynep@ornek.test", "+905343334455" },
            { "mus-004", "Emre Şahin",   "emre@ornek.test",   "+905354445566" },
            { "mus-005", "Elif Çelik",   "elif@ornek.test",   "+905365556677" },
        };

        for (var i = 0; i < people.GetLength(0); i++)
        {
            customersDb.Customers.Add(new Customer
            {
                TenantId = tenantId,
                Ref = people[i, 0],
                Name = people[i, 1],
                Email = people[i, 2],
                Phone = people[i, 3],
            });
        }

        await customersDb.SaveChangesAsync(cancellationToken);

        // 24 ödeme: her dördüncüsü başarısız, kalanı başarılı.
        const int count = 24;
        var written = new List<PaymentIntent>(count);

        for (var i = 0; i < count; i++)
        {
            var payment = PaymentIntent.Create(
                tenantId,
                profileId,
                Money.Of(14990 + (i * 3175), "TRY"),
                $"Demo sipariş #{1000 + i}",
                installments: i % 4 == 0 ? 3 : 1,
                customerRef: people[i % people.GetLength(0), 0],
                channel: "api");

            if (i % 4 == 1)
                payment.MarkFailed();
            else
                payment.MarkSucceededDirect();

            paymentsDb.PaymentIntents.Add(payment);
            written.Add(payment);
        }

        await paymentsDb.SaveChangesAsync(cancellationToken);

        // Tarihleri geriye yay. Denetim yorumlayıcısı CreatedAt'i YALNIZ Added durumunda
        // yazar; bu ikinci kayıt Modified olduğu için üzerine yazmaz.
        for (var i = 0; i < written.Count; i++)
        {
            var daysAgo = 29 - (i * 29 / Math.Max(1, written.Count - 1));
            written[i].CreatedAt = today.AddDays(-daysAgo);
        }

        await paymentsDb.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Verilen işi oturum düzeyi advisory lock altında koşturur.
    ///
    /// Her modülün kendi DbContext'i (dolayısıyla kendi bağlantısı) var; tek transaction
    /// hepsini kapsayamaz. Bu yüzden kilit, tohumlama boyunca AÇIK TUTULAN ayrı bir
    /// bağlantıda alınır. Böylece iki API kopyası aynı anda kalksa bile "işyeri var mı?"
    /// kontrolü ile yazma arasına başka kimse giremez.
    /// </summary>
    public static async Task RunLockedAsync(
        string connectionString, Func<Task> work, CancellationToken cancellationToken)
    {
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken);

        await using (var acquire = new NpgsqlCommand(LockSql, lockConnection))
            await acquire.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            await work();
        }
        finally
        {
            // Bağlantı kapanınca oturum kilitleri zaten düşer; bu açık bırakma
            // yalnız niyeti okunur kılıyor.
            await using var release = new NpgsqlCommand(UnlockSql, lockConnection);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private const string LockSql = "SELECT pg_advisory_lock(20260826)";
    private const string UnlockSql = "SELECT pg_advisory_unlock(20260826)";
}
