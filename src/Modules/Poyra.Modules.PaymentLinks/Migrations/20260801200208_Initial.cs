using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.PaymentLinks.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "text", nullable: false, computedColumnSql: "'lnk_' || replace(id::text, '-', '')", stored: true),
                    slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    max_installments = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    max_usage = table.Column<int>(type: "integer", nullable: false),
                    success_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_links", x => x.id);
                    table.CheckConstraint("ck_payment_links_amount", "amount_minor IS NULL OR amount_minor > 0");
                    table.CheckConstraint("ck_payment_links_installments", "max_installments BETWEEN 1 AND 12");
                    table.CheckConstraint("ck_payment_links_usage", "max_usage >= 0");
                });

            migrationBuilder.CreateTable(
                name: "payment_link_lookups",
                columns: table => new
                {
                    slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_link_lookups", x => x.slug);
                    table.ForeignKey(
                        name: "fk_payment_link_lookups_payment_links_payment_link_id",
                        column: x => x.payment_link_id,
                        principalTable: "payment_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_link_usages",
                columns: table => new
                {
                    payment_public_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_link_usages", x => x.payment_public_id);
                    table.ForeignKey(
                        name: "fk_payment_link_usages_payment_links_payment_link_id",
                        column: x => x.payment_link_id,
                        principalTable: "payment_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_link_lookups_payment_link_id",
                table: "payment_link_lookups",
                column: "payment_link_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_link_usages_payment_link_id_created_at",
                table: "payment_link_usages",
                columns: new[] { "payment_link_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_payment_links_public_id",
                table: "payment_links",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_links_slug",
                table: "payment_links",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_links_tenant_id_created_at",
                table: "payment_links",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_link_lookups");

            migrationBuilder.DropTable(
                name: "payment_link_usages");

            migrationBuilder.DropTable(
                name: "payment_links");
        }
    }
}
