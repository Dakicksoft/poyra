using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Payments.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskSignalsToPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "customer_ip",
                table: "payment_intents",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "customer_ref",
                table: "payment_intents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "risk_decision",
                table: "payment_intents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_intents_customer_ip_created_at",
                table: "payment_intents",
                columns: new[] { "customer_ip", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_intents_customer_ref_created_at",
                table: "payment_intents",
                columns: new[] { "customer_ref", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payment_intents_customer_ip_created_at",
                table: "payment_intents");

            migrationBuilder.DropIndex(
                name: "ix_payment_intents_customer_ref_created_at",
                table: "payment_intents");

            migrationBuilder.DropColumn(
                name: "customer_ip",
                table: "payment_intents");

            migrationBuilder.DropColumn(
                name: "customer_ref",
                table: "payment_intents");

            migrationBuilder.DropColumn(
                name: "risk_decision",
                table: "payment_intents");
        }
    }
}
