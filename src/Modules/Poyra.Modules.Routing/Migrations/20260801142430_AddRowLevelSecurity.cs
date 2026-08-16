using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Routing.Migrations
{
    /// <summary>
    /// routing_rules: RLS (İlke 4) + DELETE yasağı (İlke 3 — kural sürümleri denetim izidir;
    /// geri dönüş eski sürümü aktive etmektir, silmek değil).
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE routing_rules ENABLE ROW LEVEL SECURITY;
                ALTER TABLE routing_rules FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON routing_rules
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON routing_rules FROM poyra_app;
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
                        GRANT DELETE ON routing_rules TO poyra_app;
                    END IF;
                END
                $$;

                DROP POLICY IF EXISTS tenant_isolation ON routing_rules;
                ALTER TABLE routing_rules NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE routing_rules DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
