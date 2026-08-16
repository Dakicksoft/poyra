using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class BankadanAlacak : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_receivables",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "text", nullable: false, computedColumnSql: "'rcv_' || replace(id::text, '-', '')", stored: true),
                    connector_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_public_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payment_public_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    gross_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    installments = table.Column<int>(type: "integer", nullable: false),
                    expected_rate_bps = table.Column<int>(type: "integer", nullable: true),
                    expected_commission_minor = table.Column<long>(type: "bigint", nullable: true),
                    expected_net_minor = table.Column<long>(type: "bigint", nullable: true),
                    expected_value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    confirmed_commission_minor = table.Column<long>(type: "bigint", nullable: true),
                    confirmed_net_minor = table.Column<long>(type: "bigint", nullable: true),
                    confirmed_value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    bank_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    captured_at_server = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_receivables", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bank_receivables_public_id",
                table: "bank_receivables",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bank_receivables_tenant_id_attempt_public_id_kind",
                table: "bank_receivables",
                columns: new[] { "tenant_id", "attempt_public_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bank_receivables_tenant_id_connector_account_id",
                table: "bank_receivables",
                columns: new[] { "tenant_id", "connector_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_receivables_tenant_id_status_expected_value_date",
                table: "bank_receivables",
                columns: new[] { "tenant_id", "status", "expected_value_date" });

            migrationBuilder.Sql("""
                ALTER TABLE bank_receivables ENABLE ROW LEVEL SECURITY;
                ALTER TABLE bank_receivables FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON bank_receivables
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE bank_receivables
                    ADD CONSTRAINT fk_bank_receivables_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                -- İLKE 3. Alacak kaydı paranın iddiasıdır: silinmesi, işyerinin bankadan
                -- ne beklediğinin izini yok etmektir.
                --  · SİLİNEMEZ.
                --  · UPDATE serbesttir çünkü alacak yaşam döngüsü YAŞAR: banka teyit
                --    ettiğinde gerçek komisyon ve valör yazılır, valör geçince gecikmiş
                --    işaretlenir. Beklenen değerler (expected_*) bir kez yazılır ve
                --    DEĞİŞMEZ — bizim iddiamız sonradan düzeltilirse fark kaybolurdu.
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON bank_receivables FROM poyra_app;
                    END IF;
                END
                $$;
                """);
        }
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        GRANT DELETE ON bank_receivables TO poyra_app;
                    END IF;
                END
                $$;
                ALTER TABLE bank_receivables
                    DROP CONSTRAINT IF EXISTS fk_bank_receivables_tenants_tenant_id;
                DROP POLICY IF EXISTS tenant_isolation ON bank_receivables;
                """);

            migrationBuilder.DropTable(
                name: "bank_receivables");
        }
    }
}
