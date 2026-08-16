using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Persistence.Interceptors;

/// <summary>
/// İki katmanlı izolasyonun B katmanı: her bağlantı açılışında oturum değişkenini
/// (app.tenant_id) parametreli set_config ile yazar. RLS politikaları bu değişkeni okur:
///
///   CREATE POLICY tenant_isolation ON payment_intents FOR ALL
///     USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
///
/// İşyeri bağlamı olmayan (platform) çağrılarda değer boş string'tir → NULLIF NULL üretir → hiçbir satır
/// eşleşmez (varsayılan-ret). Npgsql havuzu bağlantıyı geri alırken oturum durumunu
/// sıfırlar; ayrıca her açılışta yeniden yazıldığı için işyerleri arası sızıntı olmaz.
/// Uygulama 'poyra_app' (BYPASSRLS'siz, sahip olmayan) rolüyle bağlanmalıdır.
/// </summary>
public sealed class RlsConnectionInterceptor(TenantContext tenant) : DbConnectionInterceptor
{
    private const string SetTenantSql = "SELECT set_config('app.tenant_id', @tenant_id, false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        var command = CreateCommand(connection);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = SetTenantSql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant_id";
        parameter.Value = tenant.HasTenant ? tenant.TenantId.ToString() : "";
        command.Parameters.Add(parameter);

        return command;
    }
}
