using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Tenancy.Migrations
{
    /// <summary>
    /// tenant_brandings RLS'lidir (iş verisi: işyerinin müşteriye görünen kimliği).
    /// Checkout kimliksiz okuyabilsin diye ITenantBrandingSource önce slug'dan işyerini
    /// çözer, sonra TenantContext kurup RLS'li satırı okur — checkout_domain eşlemesi de
    /// aynı yoldan gider. DELETE yasak: marka değiştirilir, silinmez (İlke 3).
    /// </summary>
    public partial class AddTenantBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_brandings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    primary_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    logo_bytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    logo_content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    support_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    support_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    checkout_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_brandings", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_tenant_brandings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_brandings_checkout_domain",
                table: "tenant_brandings",
                column: "checkout_domain",
                unique: true,
                filter: "checkout_domain IS NOT NULL");
            migrationBuilder.Sql("""
                ALTER TABLE tenant_brandings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenant_brandings FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON tenant_brandings
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON tenant_brandings FROM poyra_app;
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
                        GRANT DELETE ON tenant_brandings TO poyra_app;
                    END IF;
                END
                $$;

                DROP POLICY IF EXISTS tenant_isolation ON tenant_brandings;
                ALTER TABLE tenant_brandings NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE tenant_brandings DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropTable(
                name: "tenant_brandings");
        }
    }
}
