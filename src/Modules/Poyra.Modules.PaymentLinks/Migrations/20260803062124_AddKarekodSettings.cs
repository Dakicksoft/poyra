using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.PaymentLinks.Migrations
{
    /// <summary>
    /// karekod_settings işyerine aittir → RLS zorunlu. DELETE yasak: karekod basılmış
    /// bir dönemde hangi tanımlayıcının kullanıldığı, bankayla yaşanacak uyuşmazlıkta
    /// sorulacak ilk sorudur (İlke 3).
    /// </summary>
    public partial class AddKarekodSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "karekod_settings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scheme_guid = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    merchant_no = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    category_code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    merchant_name = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: true),
                    merchant_city = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_karekod_settings", x => x.tenant_id);
                });
            migrationBuilder.Sql("""
                ALTER TABLE karekod_settings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE karekod_settings FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON karekod_settings
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE karekod_settings
                    ADD CONSTRAINT fk_karekod_settings_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON karekod_settings FROM poyra_app;
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
                        GRANT DELETE ON karekod_settings TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE karekod_settings
                    DROP CONSTRAINT IF EXISTS fk_karekod_settings_tenants_tenant_id;
                DROP POLICY IF EXISTS tenant_isolation ON karekod_settings;
                ALTER TABLE karekod_settings NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE karekod_settings DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropTable(
                name: "karekod_settings");
        }
    }
}
