using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Vault.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "card_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    public_token = table.Column<string>(type: "text", nullable: false, computedColumnSql: "'tok_' || replace(id::text, '-', '')", stored: true),
                    card_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    masked_pan = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    brand = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expiry_month = table.Column<int>(type: "integer", nullable: false),
                    expiry_year = table.Column<int>(type: "integer", nullable: false),
                    holder_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_card_tokens", x => x.id);
                    table.CheckConstraint("ck_card_tokens_expiry_month", "expiry_month BETWEEN 1 AND 12");
                    table.CheckConstraint("ck_card_tokens_expiry_year", "expiry_year BETWEEN 2000 AND 2099");
                });

            migrationBuilder.CreateIndex(
                name: "ix_card_tokens_public_token",
                table: "card_tokens",
                column: "public_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_card_tokens_tenant_id_customer_ref",
                table: "card_tokens",
                columns: new[] { "tenant_id", "customer_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_card_tokens_tenant_id_customer_ref_fingerprint",
                table: "card_tokens",
                columns: new[] { "tenant_id", "customer_ref", "fingerprint" },
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "card_tokens");
        }
    }
}
