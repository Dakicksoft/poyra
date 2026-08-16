using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Subscriptions.Migrations
{
    /// <summary>
    /// Abonelik tabloları: RLS (İlke 4) + DELETE yasağı (İlke 3 — abonelik iptal edilir,
    /// faturalar ve dunning denemeleri kanıt/denetim izidir).
    /// card_tokens'a FK BİLİNÇLİ olarak kurulmaz: Kasa'da kart kaldırıldığında (kriptografik
    /// imha) abonelik kaydı ayakta kalmalı ve "kart güncelle" akışına düşmelidir.
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE plans ENABLE ROW LEVEL SECURITY;
                ALTER TABLE plans FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON plans
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE subscriptions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON subscriptions
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE subscription_invoices ENABLE ROW LEVEL SECURITY;
                ALTER TABLE subscription_invoices FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON subscription_invoices
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE plans
                    ADD CONSTRAINT fk_plans_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON plans FROM poyra_app;
                        REVOKE DELETE ON subscriptions FROM poyra_app;
                        REVOKE DELETE ON subscription_invoices FROM poyra_app;
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
                        GRANT DELETE ON plans TO poyra_app;
                        GRANT DELETE ON subscriptions TO poyra_app;
                        GRANT DELETE ON subscription_invoices TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE plans DROP CONSTRAINT IF EXISTS fk_plans_tenants_tenant_id;

                DROP POLICY IF EXISTS tenant_isolation ON subscription_invoices;
                ALTER TABLE subscription_invoices NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE subscription_invoices DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation ON subscriptions;
                ALTER TABLE subscriptions NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE subscriptions DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation ON plans;
                ALTER TABLE plans NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE plans DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
