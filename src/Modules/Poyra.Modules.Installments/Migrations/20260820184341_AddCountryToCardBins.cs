using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Installments.Migrations
{
    /// <inheritdoc />
    public partial class AddCountryToCardBins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "country",
                table: "card_bins",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: false,
                defaultValue: "TR");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "country",
                table: "card_bins");
        }
    }
}
