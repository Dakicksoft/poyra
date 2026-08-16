using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Field.Migrations
{
    /// <inheritdoc />
    public partial class SahaTahsilat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "field_agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    pin_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_field_agents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "field_collections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "text", nullable: false, computedColumnSql: "'fc_' || replace(id::text, '-', '')", stored: true),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payment_link_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    checkout_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    payment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    client_op_id = table.Column<Guid>(type: "uuid", nullable: false),
                    captured_at_device = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurred_at_server = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device_skew_seconds = table.Column<long>(type: "bigint", nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    device_claims = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_field_collections", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_field_agents_tenant_id_code",
                table: "field_agents",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_field_agents_tenant_id_device_id",
                table: "field_agents",
                columns: new[] { "tenant_id", "device_id" },
                unique: true,
                filter: "device_id IS NOT NULL AND disabled_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_field_collections_public_id",
                table: "field_collections",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_field_collections_tenant_id_agent_id_occurred_at_server",
                table: "field_collections",
                columns: new[] { "tenant_id", "agent_id", "occurred_at_server" });

            migrationBuilder.CreateIndex(
                name: "ix_field_collections_tenant_id_client_op_id",
                table: "field_collections",
                columns: new[] { "tenant_id", "client_op_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_field_collections_tenant_id_status",
                table: "field_collections",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.Sql("""
                ALTER TABLE field_agents ENABLE ROW LEVEL SECURITY;
                ALTER TABLE field_agents FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON field_agents
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE field_collections ENABLE ROW LEVEL SECURITY;
                ALTER TABLE field_collections FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON field_collections
                    FOR ALL USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE field_agents
                    ADD CONSTRAINT fk_field_agents_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);
                ALTER TABLE field_collections
                    ADD CONSTRAINT fk_field_collections_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);
                ALTER TABLE field_collections
                    ADD CONSTRAINT fk_field_collections_field_agents_agent_id
                    FOREIGN KEY (agent_id) REFERENCES field_agents (id);

                -- İLKE 3. Saha tahsilatı denetimin ilk baktığı yerdir: "bu para gerçekten
                -- toplandı mı, kim topladı, ne zaman beyan etti".
                --  · field_collections SİLİNEMEZ. UPDATE serbesttir çünkü para durumunu
                --    SUNUCU sonradan yazar (pending → succeeded/failed, İlke 2) ve
                --    çelişen cihaz iddiaları device_claims'e eklenir.
                --  · field_agents SİLİNEMEZ: işten ayrılan temsilcinin kaydı silinirse
                --    geçmiş tahsilatlar sahipsiz kalır. Kapatma DisabledAt ile yapılır.
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON field_collections FROM poyra_app;
                        REVOKE DELETE ON field_agents FROM poyra_app;
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
                        GRANT DELETE ON field_collections TO poyra_app;
                        GRANT DELETE ON field_agents TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE field_collections
                    DROP CONSTRAINT IF EXISTS fk_field_collections_field_agents_agent_id;
                ALTER TABLE field_collections
                    DROP CONSTRAINT IF EXISTS fk_field_collections_tenants_tenant_id;
                ALTER TABLE field_agents
                    DROP CONSTRAINT IF EXISTS fk_field_agents_tenants_tenant_id;
                DROP POLICY IF EXISTS tenant_isolation ON field_collections;
                DROP POLICY IF EXISTS tenant_isolation ON field_agents;
                """);

            migrationBuilder.DropTable(
                name: "field_agents");

            migrationBuilder.DropTable(
                name: "field_collections");
        }
    }
}
