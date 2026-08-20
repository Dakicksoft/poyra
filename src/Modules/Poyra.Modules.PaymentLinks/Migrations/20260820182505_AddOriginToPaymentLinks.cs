using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.PaymentLinks.Migrations
{
    /// <inheritdoc />
    public partial class AddOriginToPaymentLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "payment_links",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "link");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "origin",
                table: "payment_links");
        }
    }
}
