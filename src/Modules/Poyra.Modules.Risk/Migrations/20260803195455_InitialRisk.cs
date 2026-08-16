using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Risk.Migrations
{
    /// <inheritdoc />
    public partial class InitialRisk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blocklist_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocklist_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "risk_assessments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rule_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    rule_version = table.Column<int>(type: "integer", nullable: true),
                    flow = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    signals = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_risk_assessments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "risk_rule_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_risk_rule_sets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_blocklist_entries_tenant_id_kind_value",
                table: "blocklist_entries",
                columns: new[] { "tenant_id", "kind", "value" },
                unique: true,
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_risk_assessments_payment_id",
                table: "risk_assessments",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_risk_assessments_tenant_id_created_at",
                table: "risk_assessments",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_risk_rule_sets_tenant_id",
                table: "risk_rule_sets",
                column: "tenant_id",
                unique: true,
                filter: "active");

            migrationBuilder.CreateIndex(
                name: "ix_risk_rule_sets_tenant_id_version",
                table: "risk_rule_sets",
                columns: new[] { "tenant_id", "version" },
                unique: true);

            migrationBuilder.Sql("""
                -- Katman B: işyeri yalıtımı (İlke 4)
                ALTER TABLE risk_rule_sets ENABLE ROW LEVEL SECURITY;
                ALTER TABLE risk_rule_sets FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON risk_rule_sets
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE blocklist_entries ENABLE ROW LEVEL SECURITY;
                ALTER TABLE blocklist_entries FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON blocklist_entries
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE risk_assessments ENABLE ROW LEVEL SECURITY;
                ALTER TABLE risk_assessments FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON risk_assessments
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE risk_rule_sets
                    ADD CONSTRAINT fk_risk_rule_sets_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);
                ALTER TABLE blocklist_entries
                    ADD CONSTRAINT fk_blocklist_entries_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);
                ALTER TABLE risk_assessments
                    ADD CONSTRAINT fk_risk_assessments_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                -- İlke 3:
                --  · risk_assessments tam append-only — "o gün hangi kural bakmıştı"
                --    sorusu engellenen işlemde MUTLAKA gelir
                --  · kural setleri silinemez (yenisi yayınlanır, eskisi pasifleşir → UPDATE serbest)
                --  · kara liste kayıtları silinemez; kaldırma RemovedAt ile işaretlenir
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE UPDATE, DELETE ON risk_assessments FROM poyra_app;
                        REVOKE DELETE ON risk_rule_sets FROM poyra_app;
                        REVOKE DELETE ON blocklist_entries FROM poyra_app;
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
                        GRANT UPDATE, DELETE ON risk_assessments TO poyra_app;
                        GRANT DELETE ON risk_rule_sets TO poyra_app;
                        GRANT DELETE ON blocklist_entries TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE risk_assessments DROP CONSTRAINT IF EXISTS fk_risk_assessments_tenants_tenant_id;
                ALTER TABLE blocklist_entries DROP CONSTRAINT IF EXISTS fk_blocklist_entries_tenants_tenant_id;
                ALTER TABLE risk_rule_sets DROP CONSTRAINT IF EXISTS fk_risk_rule_sets_tenants_tenant_id;
                """);

            migrationBuilder.DropTable(
                name: "blocklist_entries");

            migrationBuilder.DropTable(
                name: "risk_assessments");

            migrationBuilder.DropTable(
                name: "risk_rule_sets");
        }
    }
}
