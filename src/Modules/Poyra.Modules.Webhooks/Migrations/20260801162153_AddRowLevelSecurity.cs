using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Webhooks.Migrations
{
    /// <summary>
    /// webhook_endpoints + webhook_deliveries: RLS (İlke 4) + DELETE yasağı (İlke 3 —
    /// teslim günlüğü kanıt belgesidir; replay yeni kayıt açar, eskisi silinmez).
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE webhook_endpoints ENABLE ROW LEVEL SECURITY;
                ALTER TABLE webhook_endpoints FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON webhook_endpoints
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE webhook_deliveries ENABLE ROW LEVEL SECURITY;
                ALTER TABLE webhook_deliveries FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON webhook_deliveries
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON webhook_endpoints FROM poyra_app;
                        REVOKE DELETE ON webhook_deliveries FROM poyra_app;
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
                        GRANT DELETE ON webhook_endpoints TO poyra_app;
                        GRANT DELETE ON webhook_deliveries TO poyra_app;
                    END IF;
                END
                $$;

                DROP POLICY IF EXISTS tenant_isolation ON webhook_deliveries;
                ALTER TABLE webhook_deliveries NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE webhook_deliveries DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation ON webhook_endpoints;
                ALTER TABLE webhook_endpoints NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE webhook_endpoints DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
