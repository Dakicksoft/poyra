using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Recon.Migrations
{
    /// <inheritdoc />
    public partial class KomisyonItirazi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "commission_claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "text", nullable: false, computedColumnSql: "'clm_' || replace(id::text, '-', '')", stored: true),
                    connector_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_from = table.Column<DateOnly>(type: "date", nullable: false),
                    period_to = table.Column<DateOnly>(type: "date", nullable: false),
                    claimed_minor = table.Column<long>(type: "bigint", nullable: false),
                    finding_count = table.Column<int>(type: "integer", nullable: false),
                    recovered_minor = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    bank_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_claims", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "commission_claim_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_claim_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_commission_claim_events_commission_claims_claim_id",
                        column: x => x.claim_id,
                        principalTable: "commission_claims",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_commission_claim_events_claim_id",
                table: "commission_claim_events",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_commission_claim_events_tenant_id_claim_id_created_at",
                table: "commission_claim_events",
                columns: new[] { "tenant_id", "claim_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_commission_claims_public_id",
                table: "commission_claims",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_commission_claims_tenant_id_status",
                table: "commission_claims",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.Sql("""
                ALTER TABLE commission_claims ENABLE ROW LEVEL SECURITY;
                ALTER TABLE commission_claims FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON commission_claims
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE commission_claim_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE commission_claim_events FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON commission_claim_events
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE commission_claims
                    ADD CONSTRAINT fk_commission_claims_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                -- İLKE 3.
                --  · commission_claim_events SALT EKLEMEdir: "ne zaman ilettik, banka ne
                --    dedi, para ne zaman geldi" sorusu aylar sonra sorulur. Değiştirilebilen
                --    bir zaman çizelgesi, kanıt değildir.
                --  · commission_claims SİLİNEMEZ: reddedilen bir itiraz da kayıttır — aynı
                --    bankayla aynı konuda ikinci kez konuşurken geçmiş cevabı bilmek gerekir.
                --    UPDATE serbest çünkü durum ve tahsilat YAŞAR.
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE UPDATE, DELETE ON commission_claim_events FROM poyra_app;
                        REVOKE DELETE ON commission_claims FROM poyra_app;
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
                        GRANT UPDATE, DELETE ON commission_claim_events TO poyra_app;
                        GRANT DELETE ON commission_claims TO poyra_app;
                    END IF;
                END
                $$;
                ALTER TABLE commission_claims DROP CONSTRAINT IF EXISTS fk_commission_claims_tenants_tenant_id;
                DROP POLICY IF EXISTS tenant_isolation ON commission_claim_events;
                DROP POLICY IF EXISTS tenant_isolation ON commission_claims;
                """);

            migrationBuilder.DropTable(
                name: "commission_claim_events");

            migrationBuilder.DropTable(
                name: "commission_claims");
        }
    }
}
