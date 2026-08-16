using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Connectors.Migrations
{
    /// <summary>
    /// connector_accounts: RLS (İlke 4) + DELETE yasağı (İlke 3 — hesap kapatılır, silinmez).
    /// Kimlik bilgileri zaten AES-GCM şifreli; RLS, şifreli blob'un bile işyerleri arası
    /// görünmesini engeller.
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE connector_accounts ENABLE ROW LEVEL SECURITY;
                ALTER TABLE connector_accounts FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON connector_accounts
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON connector_accounts FROM poyra_app;
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
                        GRANT DELETE ON connector_accounts TO poyra_app;
                    END IF;
                END
                $$;

                DROP POLICY IF EXISTS tenant_isolation ON connector_accounts;
                ALTER TABLE connector_accounts NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE connector_accounts DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
