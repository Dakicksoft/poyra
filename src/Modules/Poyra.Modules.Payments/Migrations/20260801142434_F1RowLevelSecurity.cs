using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Payments.Migrations
{
    /// <summary>
    /// F1 tabloları için:
    /// 1) RLS: payment_attempts ve refunds işyeri politikası taşır.
    ///    callback_tokens BİLİNÇLİ olarak RLS'siz platform tablosudur (api_keys emsali):
    ///    banka dönüşünde işyeri bağlamı henüz yoktur, belirteç üzerinden kurulur.
    /// 2) Append-only (İlke 3): attempts/refunds DELETE yasak (durum güncellenir, silinmez).
    /// 3) Çapraz modül FK: payment_attempts.connector_account_id → connector_accounts(id)
    ///    ham SQL ile — Connectors migration'ları önce koşmalıdır (DatabaseMigrator sırası).
    /// </summary>
    public partial class F1RowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- RLS
                ALTER TABLE payment_attempts ENABLE ROW LEVEL SECURITY;
                ALTER TABLE payment_attempts FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON payment_attempts
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE refunds ENABLE ROW LEVEL SECURITY;
                ALTER TABLE refunds FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON refunds
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                -- Çapraz modül FK (ham SQL — modeller ayrı context'lerde)
                ALTER TABLE payment_attempts
                    ADD CONSTRAINT fk_payment_attempts_connector_accounts_connector_account_id
                    FOREIGN KEY (connector_account_id) REFERENCES connector_accounts (id);

                -- Append-only: rol varsa DELETE yetkilerini geri al
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON payment_attempts FROM poyra_app;
                        REVOKE DELETE ON refunds FROM poyra_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        GRANT DELETE ON payment_attempts TO poyra_app;
                        GRANT DELETE ON refunds TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE payment_attempts
                    DROP CONSTRAINT IF EXISTS fk_payment_attempts_connector_accounts_connector_account_id;

                DROP POLICY IF EXISTS tenant_isolation ON refunds;
                ALTER TABLE refunds NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE refunds DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation ON payment_attempts;
                ALTER TABLE payment_attempts NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE payment_attempts DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
