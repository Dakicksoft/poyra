using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Compliance.Migrations
{
    /// <inheritdoc />
    public partial class InitialCompliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "suspicious_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "text", nullable: false, computedColumnSql: "'sar_' || replace(id::text, '-', '')", stored: true),
                    payment_ids = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    customer_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resolution = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    opened_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suspicious_reports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_resource_type_resource_id",
                table: "audit_log",
                columns: new[] { "resource_type", "resource_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_actor",
                table: "audit_log",
                columns: new[] { "tenant_id", "actor" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_created_at",
                table: "audit_log",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_suspicious_reports_public_id",
                table: "suspicious_reports",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_suspicious_reports_tenant_id_created_at",
                table: "suspicious_reports",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.Sql("""
                ALTER TABLE audit_log ENABLE ROW LEVEL SECURITY;
                ALTER TABLE audit_log FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON audit_log
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE suspicious_reports ENABLE ROW LEVEL SECURITY;
                ALTER TABLE suspicious_reports FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON suspicious_reports
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE audit_log
                    ADD CONSTRAINT fk_audit_log_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);
                ALTER TABLE suspicious_reports
                    ADD CONSTRAINT fk_suspicious_reports_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                -- İlke 3, en katı hali: DEĞİŞTİRİLEBİLEN denetim izi, denetim izi DEĞİLDİR.
                -- audit_log tam append-only; şüpheli işlem kaydı silinemez ama
                -- inceleme ilerlediği için UPDATE serbest.
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE UPDATE, DELETE ON audit_log FROM poyra_app;
                        REVOKE DELETE ON suspicious_reports FROM poyra_app;
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
                        GRANT UPDATE, DELETE ON audit_log TO poyra_app;
                        GRANT DELETE ON suspicious_reports TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE suspicious_reports
                    DROP CONSTRAINT IF EXISTS fk_suspicious_reports_tenants_tenant_id;
                ALTER TABLE audit_log DROP CONSTRAINT IF EXISTS fk_audit_log_tenants_tenant_id;
                """);

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "suspicious_reports");
        }
    }
}
