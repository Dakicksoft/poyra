using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Installments.Migrations
{
    /// <summary>
    /// installment_schemes: RLS (İlke 4) + DELETE yasağı (İlke 3 — şema pasife çekilir, silinmez)
    /// + çapraz modül FK connector_accounts'a (ham SQL — Connectors migration'ları önce koşar).
    /// card_bins BİLİNÇLİ olarak RLS'siz platform kataloğudur: kart programı bilgisi
    /// işyerine özgü değildir, tüm işyerleri aynı katalogdan okur.
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE installment_schemes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE installment_schemes FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON installment_schemes
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE installment_schemes
                    ADD CONSTRAINT fk_installment_schemes_connector_accounts_connector_account_id
                    FOREIGN KEY (connector_account_id) REFERENCES connector_accounts (id);

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON installment_schemes FROM poyra_app;
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
                        GRANT DELETE ON installment_schemes TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE installment_schemes
                    DROP CONSTRAINT IF EXISTS fk_installment_schemes_connector_accounts_connector_account_id;

                DROP POLICY IF EXISTS tenant_isolation ON installment_schemes;
                ALTER TABLE installment_schemes NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE installment_schemes DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
