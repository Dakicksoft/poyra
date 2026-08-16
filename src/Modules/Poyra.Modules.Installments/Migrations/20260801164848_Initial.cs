using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Installments.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "card_bins",
                columns: table => new
                {
                    bin = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    bank_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    bank_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    program = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    brand = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    card_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    is_commercial = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_card_bins", x => x.bin);
                });

            migrationBuilder.CreateTable(
                name: "installment_schemes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    installment_count = table.Column<int>(type: "integer", nullable: false),
                    customer_rate_bps = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_installment_schemes", x => x.id);
                    table.CheckConstraint("ck_installment_schemes_count", "installment_count BETWEEN 2 AND 12");
                    table.CheckConstraint("ck_installment_schemes_rate", "customer_rate_bps BETWEEN 0 AND 10000");
                });

            migrationBuilder.CreateIndex(
                name: "ix_card_bins_program",
                table: "card_bins",
                column: "program");

            migrationBuilder.CreateIndex(
                name: "ix_installment_schemes_tenant_id_connector_account_id_program_",
                table: "installment_schemes",
                columns: new[] { "tenant_id", "connector_account_id", "program", "installment_count" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "card_bins");

            migrationBuilder.DropTable(
                name: "installment_schemes");
        }
    }
}
