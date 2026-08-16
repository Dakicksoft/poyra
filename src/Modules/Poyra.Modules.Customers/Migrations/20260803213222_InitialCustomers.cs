using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Customers.Migrations
{
    /// <inheritdoc />
    public partial class InitialCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    erased_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    erased_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mandates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "text", nullable: false, computedColumnSql: "'mnd_' || replace(id::text, '-', '')", stored: true),
                    customer_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    card_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    text_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mandates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_ref",
                table: "customers",
                columns: new[] { "tenant_id", "ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mandates_public_id",
                table: "mandates",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mandates_tenant_id_customer_ref",
                table: "mandates",
                columns: new[] { "tenant_id", "customer_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_mandates_tenant_id_customer_ref_card_token",
                table: "mandates",
                columns: new[] { "tenant_id", "customer_ref", "card_token" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.Sql("""
                ALTER TABLE customers ENABLE ROW LEVEL SECURITY;
                ALTER TABLE customers FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON customers
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE mandates ENABLE ROW LEVEL SECURITY;
                ALTER TABLE mandates FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON mandates
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE customers
                    ADD CONSTRAINT fk_customers_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);
                ALTER TABLE mandates
                    ADD CONSTRAINT fk_mandates_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                -- İlke 3 ile KVKK'nın buluştuğu yer:
                --  · customers SİLİNEMEZ (ödeme/abonelik/kasa ona bağlıdır) ama
                --    UPDATE serbesttir — KVKK silme talebi tam olarak bir UPDATE'tir:
                --    kişisel veri temizlenir, kayıt ve referans kalır.
                --  · mandates silinemez; iptal RevokedAt ile işaretlenir çünkü
                --    iptalden ÖNCEKİ çekimlerin dayanağı sonradan sorulur.
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON customers FROM poyra_app;
                        REVOKE DELETE ON mandates FROM poyra_app;
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
                        GRANT DELETE ON customers TO poyra_app;
                        GRANT DELETE ON mandates TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE mandates DROP CONSTRAINT IF EXISTS fk_mandates_tenants_tenant_id;
                ALTER TABLE customers DROP CONSTRAINT IF EXISTS fk_customers_tenants_tenant_id;
                """);

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropTable(
                name: "mandates");
        }
    }
}
