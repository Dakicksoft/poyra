using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Disputes.Migrations
{
    /// <inheritdoc />
    public partial class InitialDisputes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "disputes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "text", nullable: false, computedColumnSql: "'dsp_' || replace(id::text, '-', '')", stored: true),
                    payment_intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_public_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    connector_dispute_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    connector_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    raw_reason_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    evidence_summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    due_warning_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_disputes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dispute_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispute_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dispute_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_dispute_events_disputes_dispute_id",
                        column: x => x.dispute_id,
                        principalTable: "disputes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dispute_evidences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispute_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dispute_evidences", x => x.id);
                    table.ForeignKey(
                        name: "fk_dispute_evidences_disputes_dispute_id",
                        column: x => x.dispute_id,
                        principalTable: "disputes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dispute_events_dispute_id",
                table: "dispute_events",
                column: "dispute_id");

            migrationBuilder.CreateIndex(
                name: "ix_dispute_evidences_dispute_id",
                table: "dispute_evidences",
                column: "dispute_id");

            migrationBuilder.CreateIndex(
                name: "ix_disputes_evidence_due_at",
                table: "disputes",
                column: "evidence_due_at",
                filter: "status = 'open'");

            migrationBuilder.CreateIndex(
                name: "ix_disputes_payment_intent_id",
                table: "disputes",
                column: "payment_intent_id");

            migrationBuilder.CreateIndex(
                name: "ix_disputes_public_id",
                table: "disputes",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_disputes_tenant_id",
                table: "disputes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_disputes_tenant_id_connector_dispute_id",
                table: "disputes",
                columns: new[] { "tenant_id", "connector_dispute_id" },
                unique: true,
                filter: "connector_dispute_id IS NOT NULL");

            migrationBuilder.Sql("""
                -- Katman B: işyeri yalıtımı (İlke 4)
                ALTER TABLE disputes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE disputes FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON disputes
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE dispute_evidences ENABLE ROW LEVEL SECURITY;
                ALTER TABLE dispute_evidences FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON dispute_evidences
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE dispute_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE dispute_events FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON dispute_events
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                -- Çapraz modül FK (ham SQL — modeller ayrı context'lerde)
                ALTER TABLE disputes
                    ADD CONSTRAINT fk_disputes_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);
                ALTER TABLE disputes
                    ADD CONSTRAINT fk_disputes_payment_intents_payment_intent_id
                    FOREIGN KEY (payment_intent_id) REFERENCES payment_intents (id);

                -- İlke 3: itiraz dosyası SİLİNEMEZ.
                --  · dispute_events tam append-only (iptal bile yeni bir olaydır)
                --  · disputes silinemez ama durumu ilerler → UPDATE serbest
                --  · dispute_evidences silinemez; yanlış belge RevokedAt ile iptal edilir
                --    (bytea içeriği kalır: hakem aşamasında "o gün ne yüklenmişti" sorulur)
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE UPDATE, DELETE ON dispute_events FROM poyra_app;
                        REVOKE DELETE ON disputes FROM poyra_app;
                        REVOKE DELETE ON dispute_evidences FROM poyra_app;
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
                        GRANT UPDATE, DELETE ON dispute_events TO poyra_app;
                        GRANT DELETE ON disputes TO poyra_app;
                        GRANT DELETE ON dispute_evidences TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE disputes DROP CONSTRAINT IF EXISTS fk_disputes_payment_intents_payment_intent_id;
                ALTER TABLE disputes DROP CONSTRAINT IF EXISTS fk_disputes_tenants_tenant_id;
                """);

            migrationBuilder.DropTable(
                name: "dispute_events");

            migrationBuilder.DropTable(
                name: "dispute_evidences");

            migrationBuilder.DropTable(
                name: "disputes");
        }
    }
}
