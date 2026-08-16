using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Vault.Migrations
{
    /// <summary>
    /// Kasa (M07) en hassas tablodur: RLS (İlke 4) + DELETE yasağı (İlke 3 — kaldırma,
    /// şifreli zarfın boşaltılmasıdır: kriptografik imha, kayıt izi kalır).
    /// PCI notu: bu tabloda CVV YOKTUR ve PAN yalnız AES-256-GCM zarf içinde durur;
    /// zarf anahtarı (Poyra:VaultKey) kimlik/konnektör anahtarlarından ayrıdır.
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE card_tokens ENABLE ROW LEVEL SECURITY;
                ALTER TABLE card_tokens FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON card_tokens
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE card_tokens
                    ADD CONSTRAINT fk_card_tokens_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON card_tokens FROM poyra_app;
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
                        GRANT DELETE ON card_tokens TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE card_tokens DROP CONSTRAINT IF EXISTS fk_card_tokens_tenants_tenant_id;

                DROP POLICY IF EXISTS tenant_isolation ON card_tokens;
                ALTER TABLE card_tokens NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE card_tokens DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
