using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Payments.Domain;
using Poyra.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// İlke 4'ün kanıtı: Katman A (EF global filter) VE Katman B (Postgres RLS) ayrı ayrı sınanır.
/// Ham SQL sorgusu EF filtresini bilerek atlar — satırı yalnız RLS gizleyebilir.
/// </summary>
[Collection("postgres")]
public sealed class RlsIsolationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Rls_baska_isyerinin_odemelerini_gizlemeli()
    {
        var (tenantA, tenantB) = await fixture.SeedTwoTenantsAsync();

        await using (var paymentsA = fixture.CreatePayments(PostgresFixture.TenantCtx(tenantA)))
        {
            var intent = PaymentIntent.Create(tenantA, null, Money.Of(10_000, "TRY"), "A'nın ödemesi");
            paymentsA.PaymentIntents.Add(intent);
            paymentsA.PaymentEvents.Add(PaymentEvent.For(intent, "payment.created", "test"));
            await paymentsA.SaveChangesAsync();
        }

        // İşyeri B: Katman A + B birlikte → hiçbir satır yok
        await using var paymentsB = fixture.CreatePayments(PostgresFixture.TenantCtx(tenantB));
        (await paymentsB.PaymentIntents.CountAsync()).ShouldBe(0);

        // Katman A bilerek atlanır (ham SQL) → satırı tek başına RLS gizlemeli
        var rawCount = await paymentsB.Database
            .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM payment_intents""")
            .SingleAsync();
        rawCount.ShouldBe(0L);

        // İşyeri A kendi kaydını görmeli
        await using var paymentsA2 = fixture.CreatePayments(PostgresFixture.TenantCtx(tenantA));
        (await paymentsA2.PaymentIntents.CountAsync()).ShouldBe(1);
        var rawCountA = await paymentsA2.Database
            .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM payment_intents""")
            .SingleAsync();
        rawCountA.ShouldBe(1L);
    }

    [Fact]
    public async Task Isyeri_baglami_olmayan_platform_rlsli_tablolarda_hicbir_satir_gormemeli()
    {
        var (tenantA, _) = await fixture.SeedTwoTenantsAsync();

        await using (var payments = fixture.CreatePayments(PostgresFixture.TenantCtx(tenantA)))
        {
            payments.PaymentIntents.Add(PaymentIntent.Create(tenantA, null, Money.Of(5_000, "TRY"), null));
            await payments.SaveChangesAsync();
        }

        // app.tenant_id boş → NULLIF politikası NULL üretir → varsayılan-ret
        await using var platform = fixture.CreatePayments(Poyra.SharedKernel.Tenancy.TenantContext.Platform);
        var rawCount = await platform.Database
            .SqlQueryRaw<long>("""SELECT count(*) AS "Value" FROM payment_intents""")
            .SingleAsync();
        rawCount.ShouldBe(0L);
    }

    [Fact]
    public async Task Rls_baska_isyeri_adina_insert_denemesini_reddetmeli()
    {
        var (tenantA, tenantB) = await fixture.SeedTwoTenantsAsync();

        // Bağlam A'dayken B adına satır yazmaya çalış → WITH CHECK ihlali (42501)
        await using var paymentsA = fixture.CreatePayments(PostgresFixture.TenantCtx(tenantA));
        paymentsA.PaymentIntents.Add(PaymentIntent.Create(tenantB, null, Money.Of(7_500, "TRY"), "sahte"));

        var ex = await Should.ThrowAsync<DbUpdateException>(() => paymentsA.SaveChangesAsync());
        ex.InnerException.ShouldBeOfType<Npgsql.PostgresException>()
            .SqlState.ShouldBe("42501");
    }
}
