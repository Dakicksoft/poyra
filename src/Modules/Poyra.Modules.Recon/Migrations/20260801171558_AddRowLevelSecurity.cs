using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Recon.Migrations
{
    /// <summary>
    /// Mutabakat tabloları: RLS (İlke 4) + DELETE yasağı (İlke 3 — ekstre ve bulgular
    /// kanıt belgesidir, bankaya itirazın dayanağıdır; asla silinmez).
    /// Çapraz modül FK'ler ham SQL ile: agreements/statements → connector_accounts,
    /// lines.matched_attempt_id → payment_attempts (Connectors + Payments önce koşar).
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- RLS: dört tablo da işyerine ait
                ALTER TABLE commission_agreements ENABLE ROW LEVEL SECURITY;
                ALTER TABLE commission_agreements FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON commission_agreements
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE recon_statements ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recon_statements FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON recon_statements
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE recon_statement_lines ENABLE ROW LEVEL SECURITY;
                ALTER TABLE recon_statement_lines FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON recon_statement_lines
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE commission_audit_findings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE commission_audit_findings FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON commission_audit_findings
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                -- Çapraz modül FK'ler (ham SQL — modeller ayrı context'lerde)
                ALTER TABLE commission_agreements
                    ADD CONSTRAINT fk_commission_agreements_connector_accounts_account_id
                    FOREIGN KEY (connector_account_id) REFERENCES connector_accounts (id);

                ALTER TABLE recon_statements
                    ADD CONSTRAINT fk_recon_statements_connector_accounts_account_id
                    FOREIGN KEY (connector_account_id) REFERENCES connector_accounts (id);

                ALTER TABLE recon_statement_lines
                    ADD CONSTRAINT fk_recon_statement_lines_payment_attempts_matched_attempt_id
                    FOREIGN KEY (matched_attempt_id) REFERENCES payment_attempts (id);

                -- Append-only: kanıt belgeleri silinemez
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON commission_agreements FROM poyra_app;
                        REVOKE DELETE ON recon_statements FROM poyra_app;
                        REVOKE DELETE ON recon_statement_lines FROM poyra_app;
                        REVOKE UPDATE, DELETE ON commission_audit_findings FROM poyra_app;
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
                        GRANT DELETE ON commission_agreements TO poyra_app;
                        GRANT DELETE ON recon_statements TO poyra_app;
                        GRANT DELETE ON recon_statement_lines TO poyra_app;
                        GRANT UPDATE, DELETE ON commission_audit_findings TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE recon_statement_lines
                    DROP CONSTRAINT IF EXISTS fk_recon_statement_lines_payment_attempts_matched_attempt_id;
                ALTER TABLE recon_statements
                    DROP CONSTRAINT IF EXISTS fk_recon_statements_connector_accounts_account_id;
                ALTER TABLE commission_agreements
                    DROP CONSTRAINT IF EXISTS fk_commission_agreements_connector_accounts_account_id;

                DROP POLICY IF EXISTS tenant_isolation ON commission_audit_findings;
                ALTER TABLE commission_audit_findings NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE commission_audit_findings DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation ON recon_statement_lines;
                ALTER TABLE recon_statement_lines NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE recon_statement_lines DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation ON recon_statements;
                ALTER TABLE recon_statements NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE recon_statements DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation ON commission_agreements;
                ALTER TABLE commission_agreements NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE commission_agreements DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
