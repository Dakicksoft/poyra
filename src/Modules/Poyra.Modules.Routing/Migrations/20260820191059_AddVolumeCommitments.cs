using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Routing.Migrations
{
    /// <summary>
    /// volume_commitments: hesap başına aylık hedef ciro. RLS (İlke 4) ve DELETE yasağı
    /// (İlke 3) routing_rules ile aynı — taahhüt geçmişi rota kararlarının gerekçesidir,
    /// "geçen ay neden hep Garanti'ye gitti?" sorusunun cevabı bu kayıtta durur.
    /// Kaldırma DELETE değil, is_active = false'tur.
    /// </summary>
    public partial class AddVolumeCommitments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "volume_commitments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monthly_target_minor = table.Column<long>(type: "bigint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_volume_commitments", x => x.id);
                    table.CheckConstraint("ck_volume_commitments_target", "monthly_target_minor > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_volume_commitments_tenant_id_connector_account_id",
                table: "volume_commitments",
                columns: new[] { "tenant_id", "connector_account_id" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE volume_commitments ENABLE ROW LEVEL SECURITY;
                ALTER TABLE volume_commitments FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON volume_commitments
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON volume_commitments FROM poyra_app;
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
                        GRANT DELETE ON volume_commitments TO poyra_app;
                    END IF;
                END
                $$;

                DROP POLICY IF EXISTS tenant_isolation ON volume_commitments;
                ALTER TABLE volume_commitments NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE volume_commitments DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropTable(name: "volume_commitments");
        }
    }
}
