using Npgsql;
using Poyra.Modules.Payments.Domain;
using Poyra.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// İlke 3'ün kanıtı: olay defteri ve işlem kayıtları uygulama rolü için DB düzeyinde
/// değiştirilemez/silinemezdir (GRANT geri alındı) — kod hatası bile deleyemez.
/// </summary>
[Collection("postgres")]
public sealed class AppendOnlyLedgerTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Olay_defteri_update_ve_delete_uygulama_rolune_kapali_olmali()
    {
        var (tenantA, _) = await fixture.SeedTwoTenantsAsync();

        await using (var payments = fixture.CreatePayments(PostgresFixture.TenantCtx(tenantA)))
        {
            var intent = PaymentIntent.Create(tenantA, null, Money.Of(25_000, "TRY"), null);
            payments.PaymentIntents.Add(intent);
            payments.PaymentEvents.Add(PaymentEvent.For(intent, "payment.created", "test"));
            await payments.SaveChangesAsync();
        }

        await using var connection = new NpgsqlConnection(fixture.AppCs);
        await connection.OpenAsync();

        // RLS bağlamını A'ya kur — yetki hatasının satır görünürlüğünden bağımsız olduğunu kanıtla
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", tenantA.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await AssertDenied(connection, "UPDATE payment_events SET actor = 'kurcalandi'");
        await AssertDenied(connection, "DELETE FROM payment_events");
        await AssertDenied(connection, "DELETE FROM payment_intents");
    }

    private static async Task AssertDenied(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var ex = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        ex.SqlState.ShouldBe("42501", $"Beklenen: insufficient_privilege — SQL: {sql}");
    }
}
