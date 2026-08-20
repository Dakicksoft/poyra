using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Payments.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelToPaymentIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "channel",
                table: "payment_intents",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "channel",
                table: "payment_intents");
        }
    }
}
