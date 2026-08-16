using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Payments.Migrations
{
    /// <summary>
    /// Müşterinin sağlayıcıya nasıl gideceği: TR bankaları imzalı alanlarla POST ister,
    /// Stripe/Adyen hazır bir adrese GET yönlendirmesi bekler. Mevcut kayıtların hepsi
    /// banka akışıdır, bu yüzden varsayılan "POST" — boş bırakmak anlamsız bir yöntem olurdu.
    /// </summary>
    public partial class AddRedirectMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "redirect_method",
                table: "payment_attempts",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "POST");  // mevcut kayıtlar TR bankası akışıdır
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "redirect_method",
                table: "payment_attempts");
        }
    }
}
