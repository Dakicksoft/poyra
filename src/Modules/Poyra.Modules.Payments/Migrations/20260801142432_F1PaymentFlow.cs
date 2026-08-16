using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Payments.Migrations
{
    /// <inheritdoc />
    public partial class F1PaymentFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "installments",
                table: "payment_intents",
                type: "integer",
                nullable: false,
                defaultValue: 1); // F0 döneminden kalan satırlar tek çekim sayılır (check: 1-12)

            migrationBuilder.AddColumn<string>(
                name: "return_url",
                table: "payment_intents",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "routing_result",
                table: "payment_intents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payment_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_no = table.Column<int>(type: "integer", nullable: false),
                    public_id = table.Column<string>(type: "text", nullable: false, computedColumnSql: "'att_' || replace(id::text, '-', '')", stored: true),
                    connector_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    connector_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    installments = table.Column<int>(type: "integer", nullable: false),
                    redirect_action_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    redirect_fields = table.Column<string>(type: "jsonb", nullable: true),
                    connector_txn_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    auth_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    masked_pan = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    error_unified_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    error_raw_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    error_raw_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_attempts_payment_intents_payment_intent_id",
                        column: x => x.payment_intent_id,
                        principalTable: "payment_intents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "callback_tokens",
                columns: table => new
                {
                    token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_callback_tokens", x => x.token);
                    table.ForeignKey(
                        name: "fk_callback_tokens_payment_attempts_payment_attempt_id",
                        column: x => x.payment_attempt_id,
                        principalTable: "payment_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "text", nullable: false, computedColumnSql: "'ref_' || replace(id::text, '-', '')", stored: true),
                    payment_intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    connector_refund_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_unified_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    error_raw_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    error_raw_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_amount_positive", "amount_minor > 0");
                    table.ForeignKey(
                        name: "fk_refunds_payment_attempts_payment_attempt_id",
                        column: x => x.payment_attempt_id,
                        principalTable: "payment_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_payment_intents_payment_intent_id",
                        column: x => x.payment_intent_id,
                        principalTable: "payment_intents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_intents_installments",
                table: "payment_intents",
                sql: "installments BETWEEN 1 AND 12");

            migrationBuilder.CreateIndex(
                name: "ix_callback_tokens_payment_attempt_id",
                table: "callback_tokens",
                column: "payment_attempt_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_payment_intent_id_attempt_no",
                table: "payment_attempts",
                columns: new[] { "payment_intent_id", "attempt_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_public_id",
                table: "payment_attempts",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempts_tenant_id_created_at",
                table: "payment_attempts",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_payment_attempt_id",
                table: "refunds",
                column: "payment_attempt_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_payment_intent_id",
                table: "refunds",
                column: "payment_intent_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_public_id",
                table: "refunds",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refunds_tenant_id_payment_intent_id",
                table: "refunds",
                columns: new[] { "tenant_id", "payment_intent_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "callback_tokens");

            migrationBuilder.DropTable(
                name: "refunds");

            migrationBuilder.DropTable(
                name: "payment_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_intents_installments",
                table: "payment_intents");

            migrationBuilder.DropColumn(
                name: "installments",
                table: "payment_intents");

            migrationBuilder.DropColumn(
                name: "return_url",
                table: "payment_intents");

            migrationBuilder.DropColumn(
                name: "routing_result",
                table: "payment_intents");
        }
    }
}
