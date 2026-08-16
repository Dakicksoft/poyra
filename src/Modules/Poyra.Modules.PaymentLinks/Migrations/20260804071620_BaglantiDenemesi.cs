using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.PaymentLinks.Migrations
{
    /// <inheritdoc />
    public partial class BaglantiDenemesi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_link_attempts",
                columns: table => new
                {
                    payment_public_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_link_attempts", x => x.payment_public_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_link_attempts_tenant_id_payment_link_id",
                table: "payment_link_attempts",
                columns: new[] { "tenant_id", "payment_link_id" });

            migrationBuilder.Sql("""
                ALTER TABLE payment_link_attempts ENABLE ROW LEVEL SECURITY;
                ALTER TABLE payment_link_attempts FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON payment_link_attempts
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE payment_link_attempts
                    ADD CONSTRAINT fk_payment_link_attempts_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                -- İLKE 3: deneme kaydı da SALT EKLEMEdir. Sonuç ayrı satırda (usage)
                -- durduğu için bu satırı sonradan güncellemeye ihtiyaç yoktur —
                -- "ödeme denendi" olgusu geçmişte kalmış bir gerçektir, değişmez.
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE UPDATE, DELETE ON payment_link_attempts FROM poyra_app;
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
                        GRANT UPDATE, DELETE ON payment_link_attempts TO poyra_app;
                    END IF;
                END
                $$;
                ALTER TABLE payment_link_attempts
                    DROP CONSTRAINT IF EXISTS fk_payment_link_attempts_tenants_tenant_id;
                DROP POLICY IF EXISTS tenant_isolation ON payment_link_attempts;
                """);

            migrationBuilder.DropTable(
                name: "payment_link_attempts");
        }
    }
}
