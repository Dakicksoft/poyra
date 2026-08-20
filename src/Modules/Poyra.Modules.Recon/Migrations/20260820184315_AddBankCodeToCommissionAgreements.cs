using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Recon.Migrations
{
    /// <inheritdoc />
    public partial class AddBankCodeToCommissionAgreements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_commission_agreements_tenant_id_connector_account_id_instal",
                table: "commission_agreements");

            migrationBuilder.AddColumn<string>(
                name: "bank_code",
                table: "commission_agreements",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_commission_agreements_tenant_id_connector_account_id_instal",
                table: "commission_agreements",
                columns: new[] { "tenant_id", "connector_account_id", "installment_count", "bank_code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_commission_agreements_tenant_id_connector_account_id_instal",
                table: "commission_agreements");

            migrationBuilder.DropColumn(
                name: "bank_code",
                table: "commission_agreements");

            migrationBuilder.CreateIndex(
                name: "ix_commission_agreements_tenant_id_connector_account_id_instal",
                table: "commission_agreements",
                columns: new[] { "tenant_id", "connector_account_id", "installment_count" },
                unique: true);
        }
    }
}
