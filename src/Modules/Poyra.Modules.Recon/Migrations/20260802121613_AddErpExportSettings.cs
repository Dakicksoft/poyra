using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Recon.Migrations
{
    /// <summary>
    /// erp_export_settings işyerine ait muhasebe hesap eşlemesidir → RLS zorunlu.
    /// DELETE yasak: ayarın geçmişi, üretilmiş fişlerin hangi hesaplarla yazıldığının
    /// açıklamasıdır — muhasebe denetiminde sorulur (İlke 3).
    /// </summary>
    public partial class AddErpExportSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erp_export_settings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    pos_receivable_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    bank_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    commission_expense_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    document_prefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erp_export_settings", x => x.tenant_id);
                });
            migrationBuilder.Sql("""
                ALTER TABLE erp_export_settings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE erp_export_settings FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON erp_export_settings
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE erp_export_settings
                    ADD CONSTRAINT fk_erp_export_settings_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON erp_export_settings FROM poyra_app;
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
                        GRANT DELETE ON erp_export_settings TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE erp_export_settings
                    DROP CONSTRAINT IF EXISTS fk_erp_export_settings_tenants_tenant_id;
                DROP POLICY IF EXISTS tenant_isolation ON erp_export_settings;
                ALTER TABLE erp_export_settings NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE erp_export_settings DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropTable(
                name: "erp_export_settings");
        }
    }
}
