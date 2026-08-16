using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Vault.Migrations
{
    /// <summary>
    /// PCI kapsam daraltma: kart sahibi adı (CHD) düz metin sütundan kaldırılır.
    /// Ad zaten AES-256-GCM zarfının içindedir — ikinci bir düz kopya, PAN'ı şifreleyip
    /// adı açıkta bırakan tutarsız bir duruma yol açıyordu. Bulgu, PCI kanıt paketinin
    /// veritabanı taramasından çıktı (PciRuntimeEvidenceTests).
    /// </summary>
    public partial class DropPlaintextHolderName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "holder_name",
                table: "card_tokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "holder_name",
                table: "card_tokens",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }
    }
}
