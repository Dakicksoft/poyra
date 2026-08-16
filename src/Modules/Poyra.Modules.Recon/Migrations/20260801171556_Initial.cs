using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Recon.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "commission_agreements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_count = table.Column<int>(type: "integer", nullable: false),
                    rate_bps = table.Column<int>(type: "integer", nullable: false),
                    valor_days = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_agreements", x => x.id);
                    table.CheckConstraint("ck_commission_agreements_count", "installment_count BETWEEN 1 AND 12");
                    table.CheckConstraint("ck_commission_agreements_rate", "rate_bps BETWEEN 0 AND 10000");
                });

            migrationBuilder.CreateTable(
                name: "recon_statements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    matched_count = table.Column<int>(type: "integer", nullable: false),
                    missing_in_poyra_count = table.Column<int>(type: "integer", nullable: false),
                    amount_mismatch_count = table.Column<int>(type: "integer", nullable: false),
                    missing_in_statement_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recon_statements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recon_statement_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    commission_minor = table.Column<long>(type: "bigint", nullable: false),
                    net_minor = table.Column<long>(type: "bigint", nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    match_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    matched_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recon_statement_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_recon_statement_lines_recon_statements_statement_id",
                        column: x => x.statement_id,
                        principalTable: "recon_statements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commission_audit_findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installments = table.Column<int>(type: "integer", nullable: false),
                    gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    actual_commission_minor = table.Column<long>(type: "bigint", nullable: false),
                    expected_commission_minor = table.Column<long>(type: "bigint", nullable: true),
                    agreed_rate_bps = table.Column<int>(type: "integer", nullable: true),
                    delta_minor = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commission_audit_findings", x => x.id);
                    table.ForeignKey(
                        name: "fk_commission_audit_findings_recon_statement_lines_statement_l",
                        column: x => x.statement_line_id,
                        principalTable: "recon_statement_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_commission_audit_findings_recon_statements_statement_id",
                        column: x => x.statement_id,
                        principalTable: "recon_statements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_commission_agreements_tenant_id_connector_account_id_instal",
                table: "commission_agreements",
                columns: new[] { "tenant_id", "connector_account_id", "installment_count" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_commission_audit_findings_statement_id",
                table: "commission_audit_findings",
                column: "statement_id");

            migrationBuilder.CreateIndex(
                name: "ix_commission_audit_findings_statement_line_id",
                table: "commission_audit_findings",
                column: "statement_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_commission_audit_findings_tenant_id_statement_id",
                table: "commission_audit_findings",
                columns: new[] { "tenant_id", "statement_id" });

            migrationBuilder.CreateIndex(
                name: "ix_recon_statement_lines_statement_id_line_no",
                table: "recon_statement_lines",
                columns: new[] { "statement_id", "line_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recon_statement_lines_tenant_id_order_id",
                table: "recon_statement_lines",
                columns: new[] { "tenant_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_recon_statements_tenant_id_connector_account_id_statement_d",
                table: "recon_statements",
                columns: new[] { "tenant_id", "connector_account_id", "statement_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "commission_agreements");

            migrationBuilder.DropTable(
                name: "commission_audit_findings");

            migrationBuilder.DropTable(
                name: "recon_statement_lines");

            migrationBuilder.DropTable(
                name: "recon_statements");
        }
    }
}
