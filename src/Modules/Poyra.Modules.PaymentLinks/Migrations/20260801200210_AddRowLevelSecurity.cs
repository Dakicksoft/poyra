using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.PaymentLinks.Migrations
{
    /// <summary>
    /// payment_links + payment_link_usages: RLS (İlke 4) ve DELETE yasağı (İlke 3 — link
    /// kapatılır, silinmez; kullanım defteri kanıttır, ödemenin hangi linkten geldiğini taşır).
    ///
    /// payment_link_lookups BİLİNÇLİ olarak RLS'SİZDİR — api_keys / callback_tokens / users ile
    /// aynı gerekçe: slug'dan işyerini çözmek TAVUK-YUMURTA problemidir. Checkout host'u
    /// kimliksizdir; `app.tenant_id` daha ayarlanmamışken bu satırı okuyabilmelidir. Tablo
    /// yalnız (slug → tenant_id, payment_link_id) taşır; tutar, açıklama, durum gibi hiçbir iş
    /// verisi burada DURMAZ. Slug 96 bit kriptografik rastgeledir; sızdırdığı tek bilgi
    /// "bu slug şu işyerine ait"tir ve linki bilen zaten checkout sayfasını görebilir.
    /// UPDATE de yasaklanır: slug ↔ işyeri eşleşmesi asla değişmemelidir.
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE payment_links ENABLE ROW LEVEL SECURITY;
                ALTER TABLE payment_links FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON payment_links
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE payment_link_usages ENABLE ROW LEVEL SECURITY;
                ALTER TABLE payment_link_usages FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON payment_link_usages
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE payment_links
                    ADD CONSTRAINT fk_payment_links_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                ALTER TABLE payment_link_lookups
                    ADD CONSTRAINT fk_payment_link_lookups_tenants_tenant_id
                    FOREIGN KEY (tenant_id) REFERENCES tenants (id);

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poyra_app') THEN
                        REVOKE DELETE ON payment_links FROM poyra_app;
                        REVOKE UPDATE, DELETE ON payment_link_usages FROM poyra_app;
                        REVOKE UPDATE, DELETE ON payment_link_lookups FROM poyra_app;
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
                        GRANT DELETE ON payment_links TO poyra_app;
                        GRANT UPDATE, DELETE ON payment_link_usages TO poyra_app;
                        GRANT UPDATE, DELETE ON payment_link_lookups TO poyra_app;
                    END IF;
                END
                $$;

                ALTER TABLE payment_link_lookups
                    DROP CONSTRAINT IF EXISTS fk_payment_link_lookups_tenants_tenant_id;
                ALTER TABLE payment_links DROP CONSTRAINT IF EXISTS fk_payment_links_tenants_tenant_id;

                DROP POLICY IF EXISTS tenant_isolation ON payment_link_usages;
                ALTER TABLE payment_link_usages NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE payment_link_usages DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS tenant_isolation ON payment_links;
                ALTER TABLE payment_links NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE payment_links DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
