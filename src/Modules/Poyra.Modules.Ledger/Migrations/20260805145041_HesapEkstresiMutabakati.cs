using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class HesapEkstresiMutabakati : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "settlement_statements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "text", nullable: false, computedColumnSql: "'set_' || replace(id::text, '-', '')", stored: true),
                    connector_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    from_date = table.Column<DateOnly>(type: "date", nullable: true),
                    to_date = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settlement_statements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settlement_findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expected_minor = table.Column<long>(type: "bigint", nullable: false),
                    actual_minor = table.Column<long>(type: "bigint", nullable: false),
                    delta_minor = table.Column<long>(type: "bigint", nullable: false),
                    receivable_count = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settlement_findings", x => x.id);
                    table.ForeignKey(
                        name: "fk_settlement_findings_settlement_statements_statement_id",
                        column: x => x.statement_id,
                        principalTable: "settlement_statements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "settlement_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settlement_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_settlement_lines_settlement_statements_statement_id",
                        column: x => x.statement_id,
                        principalTable: "settlement_statements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_settlement_findings_statement_id",
                table: "settlement_findings",
                column: "statement_id");

            migrationBuilder.CreateIndex(
                name: "ix_settlement_findings_tenant_id_outcome_value_date",
                table: "settlement_findings",
                columns: new[] { "tenant_id", "outcome", "value_date" });

            migrationBuilder.CreateIndex(
                name: "ix_settlement_findings_tenant_id_statement_id_value_date",
                table: "settlement_findings",
                columns: new[] { "tenant_id", "statement_id", "value_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_settlement_lines_statement_id",
                table: "settlement_lines",
                column: "statement_id");

            migrationBuilder.CreateIndex(
                name: "ix_settlement_lines_tenant_id_statement_id_value_date",
                table: "settlement_lines",
                columns: new[] { "tenant_id", "statement_id", "value_date" });

            migrationBuilder.CreateIndex(
                name: "ix_settlement_statements_public_id",
                table: "settlement_statements",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_settlement_statements_tenant_id_connector_account_id",
                table: "settlement_statements",
                columns: new[] { "tenant_id", "connector_account_id" });

            migrationBuilder.Sql("""
                ALTER TABLE settlement_statements ENABLE ROW LEVEL SECURITY;
                ALTER TABLE settlement_statements FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON settlement_statements
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE settlement_lines ENABLE ROW LEVEL SECURITY;
                ALTER TABLE settlement_lines FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON settlement_lines
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE settlement_findings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE settlement_findings FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON settlement_findings
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                -- İşyeri FK'ları ham SQL'de: modüller arası, EF modeli bilmez
                ALTER TABLE settlement_statements
                    ADD CONSTRAINT fk_settlement_statements_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                -- İLKE 3. Bulgu, bankayla yapılacak görüşmenin DAYANAĞIDIR:
                -- sonradan değiştirilebilen bir kanıt, kanıt değildir.
                --  · settlement_findings ve settlement_lines: UPDATE + DELETE YASAK.
                --    Yeni bilgi geldiğinde eski bulgu silinmez, yeni bulgu yazılır.
                --  · settlement_statements: yalnız silinemez.
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE UPDATE, DELETE ON settlement_findings FROM poyra_app;
                        REVOKE UPDATE, DELETE ON settlement_lines FROM poyra_app;
                        REVOKE DELETE ON settlement_statements FROM poyra_app;
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
                        GRANT UPDATE, DELETE ON settlement_findings TO poyra_app;
                        GRANT UPDATE, DELETE ON settlement_lines TO poyra_app;
                        GRANT DELETE ON settlement_statements TO poyra_app;
                    END IF;
                END
                $$;
                ALTER TABLE settlement_statements
                    DROP CONSTRAINT IF EXISTS fk_settlement_statements_tenants_tenant_id;
                DROP POLICY IF EXISTS tenant_isolation ON settlement_findings;
                DROP POLICY IF EXISTS tenant_isolation ON settlement_lines;
                DROP POLICY IF EXISTS tenant_isolation ON settlement_statements;
                """);

            migrationBuilder.DropTable(
                name: "settlement_findings");

            migrationBuilder.DropTable(
                name: "settlement_lines");

            migrationBuilder.DropTable(
                name: "settlement_statements");
        }
    }
}
