using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Payments;
using Poyra.Modules.Subscriptions;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Vault;
using Poyra.Persistence;
using Poyra.SharedKernel.Tenancy;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Architecture;

/// <summary>
/// PCI KANIT PAKETİ — yapısal (statik) kanıtlar.
///
/// Bu testler denetçiye "yapmıyoruz" demek yerine "yapamayız"ı gösterir: kart doğrulama
/// değeri saklanacak bir SÜTUN YOKTUR, düz PAN taşıyan bir alan YOKTUR, kart nesnesi
/// günlüğe yazılamaz. Bir geliştirici bunları geri getirirse derleme geçer ama BU TESTLER
/// KIRILIR — kanıt, süreç belgesi değil, koşan bir denetimdir.
///
/// Kapsam sınırı dürüstçe: bu paket UYGULAMA katmanını kanıtlar. Ağ segmentasyonu,
/// fiziksel güvenlik, anahtar saklama donanımı (HSM), personel ve politika gereksinimleri
/// otomatik kanıtlanamaz; docs/04-pci-kanit-paketi.md bunları ayrıca listeler.
/// </summary>
public sealed class PciEvidenceTests
{
    /// <summary>Saklanması PCI DSS tarafından KESİNLİKLE yasak olan doğrulama verisi adları.</summary>
    private static readonly string[] ForbiddenColumnNames =
        ["cvv", "cvc", "cvv2", "cvc2", "cid", "cav2", "security_code", "securitycode", "pin", "pin_block"];

    /// <summary>Düz PAN tutabilecek sütun adları — şifreli zarf dışında hiçbiri olmamalı.</summary>
    private static readonly string[] ForbiddenPanColumnNames =
        ["pan", "card_number", "cardnumber", "card_no", "kart_no", "primary_account_number"];

    private static IEnumerable<DbContext> AllContexts()
    {
        const string dummy = "Host=localhost;Database=poyra;Username=x;Password=y";
        var tenant = TenantContext.Platform;

        yield return TenancyDbContext.CreateForMigrations(dummy);
        yield return PaymentsDbContext.CreateForMigrations(dummy);
        yield return VaultDbContext.CreateForMigrations(dummy);
        yield return SubscriptionsDbContext.CreateForMigrations(dummy);
    }

    private static IEnumerable<(string Table, string Column, IProperty Property)> AllColumns()
    {
        foreach (var context in AllContexts())
        {
            using (context)
            {
                foreach (var entity in context.Model.GetEntityTypes())
                {
                    var table = entity.GetTableName() ?? entity.ShortName();
                    foreach (var property in entity.GetProperties())
                        yield return (table, property.GetColumnName().ToLowerInvariant(), property);
                }
            }
        }
    }

    // ---- 3.2 Doğrulama verisi yetkilendirmeden sonra saklanmaz -----------------

    [Fact]
    public void Hicbir_tabloda_kart_dogrulama_degeri_sutunu_olmamali()
    {
        var offenders = AllColumns()
            .Where(c => ForbiddenColumnNames.Contains(c.Column))
            .Select(c => $"{c.Table}.{c.Column}")
            .ToList();

        // CVV/CVC/PIN saklanamaz: sütun YOKSA yanlışlıkla yazmak da mümkün değildir
        offenders.ShouldBeEmpty(
            "PCI DSS 3.2: kart doğrulama değeri (CVV/CVC/PIN) hiçbir yerde saklanamaz. "
            + "Bulunan sütunlar: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Kart_nesnesinde_cvv_alani_var_ama_hicbir_varlikta_yok()
    {
        // CardData.Cvv YALNIZ bellekte taşınır — bu bilinçlidir (bankaya gönderilir).
        typeof(CardData).GetProperty("Cvv").ShouldNotBeNull();

        // Ama hiçbir EF varlığında CVV/kart nesnesi TAŞINMAZ
        var entityTypes = AllContexts().SelectMany(c =>
        {
            using (c)
                return c.Model.GetEntityTypes().Select(e => e.ClrType).ToList();
        }).ToList();

        foreach (var type in entityTypes)
        {
            type.GetProperties()
                .Where(p => p.PropertyType == typeof(CardData))
                .ShouldBeEmpty($"{type.Name} kart nesnesini taşıyamaz — kart yalnız bellekte yaşar.");

            type.GetProperties()
                .Where(p => ForbiddenColumnNames.Contains(p.Name.ToLowerInvariant()))
                .ShouldBeEmpty($"{type.Name} doğrulama verisi alanı taşıyamaz.");
        }
    }

    // ---- 3.4/3.5 PAN okunamaz saklanır ----------------------------------------

    [Fact]
    public void Duz_PAN_sutunu_hicbir_tabloda_olmamali()
    {
        var offenders = AllColumns()
            .Where(c => ForbiddenPanColumnNames.Contains(c.Column))
            .Select(c => $"{c.Table}.{c.Column}")
            .ToList();

        offenders.ShouldBeEmpty(
            "PCI DSS 3.4: PAN okunabilir saklanamaz. Kasa yalnız AES-256-GCM zarfı "
            + "(card_encrypted) ve maskeli görünüm (masked_pan) tutar. Bulunan: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Kart_tokeni_yalniz_zarf_maske_ve_parmak_izi_tasimali()
    {
        using var vault = VaultDbContext.CreateForMigrations("Host=localhost;Database=poyra;Username=x;Password=y");
        var entity = vault.Model.GetEntityTypes().Single(e => e.ClrType.Name == "CardToken");
        var columns = entity.GetProperties().Select(p => p.GetColumnName()).ToList();

        columns.ShouldContain("card_encrypted"); // AES-256-GCM zarfı
        columns.ShouldContain("masked_pan");     // ilk 6 + son 4 (PCI'ın izin verdiği azami)
        columns.ShouldContain("fingerprint");    // HMAC — tekilleştirme, PAN türetilemez

        // Zarf BAYT dizisidir: metin sütunu olsaydı log/dump'ta okunabilir görünürdü
        entity.GetProperties().Single(p => p.GetColumnName() == "card_encrypted")
            .ClrType.ShouldBe(typeof(byte[]));
    }

    [Fact]
    public void Maskeleme_yalniz_ilk_alti_ve_son_dordu_birakmali()
    {
        var masked = CardNumbers.Mask("4155650100416111");

        masked.ShouldBe("415565******6111");
        masked.ShouldNotContain("0100"); // ortadaki haneler kaybolur
        CardNumbers.Mask("41556").ShouldBe("*****"); // kısa girdi tamamen maskelenir
    }

    [Fact]
    public void Kart_nesnesi_gunluge_yazilamaz()
    {
        var card = new CardData("4155650100416111", 12, 2030, "AYSE YILMAZ", "123");

        // ToString PAN sızdırmaz — string interpolation ve logger çağrıları bunu kullanır
        var text = card.ToString();
        text.ShouldNotContain("4155650100416111");
        text.ShouldNotContain("123");
        text.ShouldContain("415565******6111");

        // $"{card}" ve logger'ın yapısal biçimlendirmesi aynı yolu izler
        $"kart: {card}".ShouldNotContain("4155650100416111");
    }

    // ---- 3.2 Kaynak taraması: PAN/CVV günlüğe verilmiyor ----------------------

    [Fact]
    public void Kaynakta_PAN_veya_CVV_gunluge_yazan_cagri_olmamali()
    {
        // Derlenmiş kod bunu kanıtlayamaz — kaynak taranır. Yalancı pozitifi düşürmek için
        // yalnız "logger.Log*(… .Pan …)" kalıbı aranır.
        var pattern = new Regex(
            @"(Log(Information|Warning|Error|Debug|Trace|Critical)|Console\.(Write|WriteLine))\s*\([^)]*\.(Pan|Cvv)\b",
            RegexOptions.Compiled);

        var offenders = SourceFiles()
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (file, no: index + 1, line))
                .Where(x => pattern.IsMatch(x.line)))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.no}")
            .ToList();

        offenders.ShouldBeEmpty(
            "PAN/CVV günlüğe yazılamaz. İhlal: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Kaynakta_kart_verisi_iceren_api_yaniti_olmamali()
    {
        // Yanıt DTO'ları (record) düz PAN/CVV alanı taşımamalı — istemciye kart geri dönmez
        var responseTypes = new[]
            {
                typeof(PaymentsModule).Assembly,
                typeof(VaultModule).Assembly,
                typeof(SubscriptionsModule).Assembly,
            }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name.EndsWith("Response", StringComparison.Ordinal)
                        || t.Name.EndsWith("Dto", StringComparison.Ordinal))
            .ToList();

        responseTypes.ShouldNotBeEmpty("yanıt tipleri bulunamadıysa test anlamsızdır");

        var offenders = responseTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => IsCardCarrying(p))
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        offenders.ShouldBeEmpty("API yanıtı kart verisi taşıyamaz: " + string.Join(", ", offenders));
    }

    private static bool IsCardCarrying(PropertyInfo property)
    {
        if (property.PropertyType == typeof(CardData))
            return true;

        var name = property.Name.ToLowerInvariant();
        if (ForbiddenColumnNames.Contains(name))
            return true;

        // "MaskedPan" serbesttir; "Pan"/"CardNumber" değildir
        return name is "pan" or "cardnumber" or "cardno";
    }

    // ---- 8.x / 3.6 Sır ve anahtar hijyeni --------------------------------------

    [Fact]
    public void Depoda_gercek_sir_olmamali()
    {
        // Örnek/geliştirme anahtarları appsettings.Development.json'dadır ve BİLİNÇLİDİR;
        // üretim appsettings.json'ları BOŞ olmalı — dolu bırakmak sırrı depoya yazmaktır.
        foreach (var file in Directory.GetFiles(RepoRoot(), "appsettings.json", SearchOption.AllDirectories)
                     .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")))
        {
            var content = File.ReadAllText(file);
            foreach (var key in new[] { "CredentialKey", "JwtKey", "VaultKey" })
            {
                var match = Regex.Match(content, $"\"{key}\"\\s*:\\s*\"([^\"]*)\"");
                if (!match.Success)
                    continue;

                match.Groups[1].Value.ShouldBeEmpty(
                    $"{Path.GetFileName(Path.GetDirectoryName(file))}/appsettings.json içinde "
                    + $"{key} DOLU — üretim sırrı depoya yazılamaz, ortam değişkeninden gelmeli.");
            }
        }
    }

    [Fact]
    public void Kasa_anahtari_konnektor_anahtarindan_ayri_olmali()
    {
        // Aynı anahtarı paylaşmak, konnektör sırrı sızınca kart zarfını da açardı
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src/Modules/Poyra.Modules.Vault/Infrastructure/CardVault.cs"));

        source.ShouldContain("Poyra:VaultKey");
        source.ShouldContain("32"); // 32 bayt = AES-256
    }

    // ---- Yardımcılar ----------------------------------------------------------

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Poyra.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("depo kökü bulunamadı (Poyra.slnx)");
        return directory.FullName;
    }

    private static IEnumerable<string> SourceFiles()
        => Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
}
