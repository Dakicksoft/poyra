using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Poyra.Modules.Tenancy.Migrations
{
    /// <summary>
    /// İki katmanlı işyeri izolasyonunun B katmanı (İlke 4).
    /// İşyerine ait tablolar RLS politikası taşır; politika, bağlantı açılışında
    /// RlsConnectionInterceptor'ın yazdığı app.tenant_id oturum değişkenini okur.
    /// Boş/eksik değişken NULLIF ile NULL'a düşer → varsayılan-ret (hiçbir satır görünmez).
    /// Not: organizations / tenants / api_keys platform tablolarıdır — api_keys RLS taşıyamaz,
    /// çünkü işyeri çözümlemesi bu tablo ÜZERİNDEN yapılır (tavuk-yumurta).
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE business_profiles ENABLE ROW LEVEL SECURITY;
                ALTER TABLE business_profiles FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON business_profiles
                    FOR ALL
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation ON business_profiles;
                ALTER TABLE business_profiles NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE business_profiles DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
