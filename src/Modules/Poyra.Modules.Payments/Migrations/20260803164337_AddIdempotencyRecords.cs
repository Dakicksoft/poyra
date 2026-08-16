using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Payments.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_tenant_id_key",
                table: "idempotency_records",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.Sql("""
                -- Katman B: işyeri yalıtımı burada da geçerli — bir işyerinin anahtarı
                -- başkasınınkiyle çakışmaz ve başkasının yanıtı okunamaz.
                ALTER TABLE idempotency_records ENABLE ROW LEVEL SECURITY;
                ALTER TABLE idempotency_records FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON idempotency_records
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE idempotency_records
                    ADD CONSTRAINT fk_idempotency_records_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                -- Bilinçli olarak append-only DEĞİL: kayıt önce açılır, yanıt gelince
                -- tamamlanır (UPDATE); 5xx'te silinir ki tekrar denenebilsin (DELETE).
                -- Operasyonel bir defter değil, teknik bir kilittir.
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation ON idempotency_records;
                ALTER TABLE idempotency_records
                    DROP CONSTRAINT IF EXISTS fk_idempotency_records_tenants_tenant_id;
                """);

            migrationBuilder.DropTable(
                name: "idempotency_records");
        }
    }
}
