using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class ValorMaliyeti : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ledger_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    annual_financing_rate_bps = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_settings_tenant_id",
                table: "ledger_settings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE ledger_settings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ledger_settings FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON ledger_settings
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE ledger_settings
                    ADD CONSTRAINT fk_ledger_settings_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                -- Ayar satırı silinemez; oran DEĞİŞEBİLİR (işyerinin kredi maliyeti
                -- zamanla değişir) — bu yüzden UPDATE serbest.
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON ledger_settings FROM poyra_app;
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
                        GRANT DELETE ON ledger_settings TO poyra_app;
                    END IF;
                END
                $$;
                ALTER TABLE ledger_settings DROP CONSTRAINT IF EXISTS fk_ledger_settings_tenants_tenant_id;
                DROP POLICY IF EXISTS tenant_isolation ON ledger_settings;
                """);

            migrationBuilder.DropTable(
                name: "ledger_settings");
        }
    }
}
