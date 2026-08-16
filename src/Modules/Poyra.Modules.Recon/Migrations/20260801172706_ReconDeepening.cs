using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Recon.Migrations
{
    /// <inheritdoc />
    public partial class ReconDeepening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "refund_line_count",
                table: "recon_statements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "expected_value_date",
                table: "recon_statement_lines",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "line_type",
                table: "recon_statement_lines",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "matched_refund_id",
                table: "recon_statement_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "valor_delta_days",
                table: "recon_statement_lines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_recon_statements_status",
                table: "recon_statements",
                column: "status",
                filter: "status = 'matching'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_recon_statements_status",
                table: "recon_statements");

            migrationBuilder.DropColumn(
                name: "refund_line_count",
                table: "recon_statements");

            migrationBuilder.DropColumn(
                name: "expected_value_date",
                table: "recon_statement_lines");

            migrationBuilder.DropColumn(
                name: "line_type",
                table: "recon_statement_lines");

            migrationBuilder.DropColumn(
                name: "matched_refund_id",
                table: "recon_statement_lines");

            migrationBuilder.DropColumn(
                name: "valor_delta_days",
                table: "recon_statement_lines");
        }
    }
}
