using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Recon.Migrations
{
    /// <inheritdoc />
    public partial class ReconFinalTouches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "refund_commission_sum",
                table: "recon_statements",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "bank_holidays",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_holidays", x => x.day);
                });

            migrationBuilder.Sql("""
                -- Yeniden eşleştirme yarışına karşı DB güvencesi: satır başına tek bulgu
                CREATE UNIQUE INDEX ux_commission_audit_findings_line
                    ON commission_audit_findings (statement_line_id);

                -- 2026 TR banka tatilleri tohumu. Dini bayramlar hicri takvimle kayar —
                -- tarihler ÖNGÖRÜDÜR, Diyanet takvimi kesinleşince /v1/bank-holidays ile düzeltilir.
                -- (Arefe yarım günleri tam iş günü sayılır; yarım gün desteği F3.)
                INSERT INTO bank_holidays (day, name, created_at) VALUES
                    ('2026-01-01', 'Yılbaşı', now()),
                    ('2026-03-20', 'Ramazan Bayramı 1. Gün (öngörü)', now()),
                    ('2026-03-21', 'Ramazan Bayramı 2. Gün (öngörü)', now()),
                    ('2026-03-22', 'Ramazan Bayramı 3. Gün (öngörü)', now()),
                    ('2026-04-23', 'Ulusal Egemenlik ve Çocuk Bayramı', now()),
                    ('2026-05-01', 'Emek ve Dayanışma Günü', now()),
                    ('2026-05-19', 'Atatürk''ü Anma, Gençlik ve Spor Bayramı', now()),
                    ('2026-05-27', 'Kurban Bayramı 1. Gün (öngörü)', now()),
                    ('2026-05-28', 'Kurban Bayramı 2. Gün (öngörü)', now()),
                    ('2026-05-29', 'Kurban Bayramı 3. Gün (öngörü)', now()),
                    ('2026-05-30', 'Kurban Bayramı 4. Gün (öngörü)', now()),
                    ('2026-07-15', 'Demokrasi ve Millî Birlik Günü', now()),
                    ('2026-08-30', 'Zafer Bayramı', now()),
                    ('2026-10-29', 'Cumhuriyet Bayramı', now())
                ON CONFLICT (day) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_commission_audit_findings_line;");

            migrationBuilder.DropTable(
                name: "bank_holidays");

            migrationBuilder.DropColumn(
                name: "refund_commission_sum",
                table: "recon_statements");
        }
    }
}
