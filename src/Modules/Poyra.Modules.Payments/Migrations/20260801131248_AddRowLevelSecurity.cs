using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Payments.Migrations
{
    /// <summary>
    /// 1) RLS (İlke 4): payment_intents ve payment_events işyeri politikası taşır.
    /// 2) Append-only (İlke 3): uygulama rolünün olay defterinde UPDATE/DELETE,
    ///    işlem kayıtlarında DELETE yetkisi DB düzeyinde geri alınır — kod hatası bile silemez.
    /// 3) Çapraz modül FK: payment_intents.tenant_id → tenants(id). Modeller ayrı DbContext'lerde
    ///    olduğundan FK bilinçli olarak ham SQL ile kurulur (modül sınırı — İlke 1).
    ///    Bu nedenle Tenancy migration'ları HER ZAMAN önce koşmalıdır (DatabaseMigrator sırası).
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- RLS
                ALTER TABLE payment_intents ENABLE ROW LEVEL SECURITY;
                ALTER TABLE payment_intents FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON payment_intents
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE payment_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE payment_events FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON payment_events
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                -- Çapraz modül FK (ham SQL — modeller ayrı context'lerde)
                ALTER TABLE payment_intents
                    ADD CONSTRAINT fk_payment_intents_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                -- Append-only: rol varsa yetkileri geri al (rol, initdb/test fikstürü tarafından kurulur)
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE UPDATE, DELETE ON payment_events FROM poyra_app;
                        REVOKE DELETE ON payment_intents FROM poyra_app;
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
                        GRANT UPDATE, DELETE ON payment_events TO poyra_app;
                        GRANT DELETE ON payment_intents TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE payment_intents
                    DROP CONSTRAINT IF EXISTS fk_payment_intents_tenants_tenant_id;

                DROP POLICY IF EXISTS tenant_isolation ON payment_events;
                ALTER TABLE payment_events NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE payment_events DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation ON payment_intents;
                ALTER TABLE payment_intents NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE payment_intents DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
