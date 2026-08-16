using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Poyra.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Architecture;

/// <summary>
/// İlke 4'ün bekçisi: işyeri verisi taşıyan her tablo İKİ katmanla korunmalıdır —
/// EF global query filter (Katman A) ve Postgres RLS (Katman B).
///
/// Bu test tek katmana düşmüş tabloyu yakalar. Yazılma sebebi gerçek bir bulgudur:
/// <c>IdempotencyRecord</c> RLS altındaydı ama <see cref="ITenantOwned"/> uygulamadığı
/// için EF filtresi ona işlemiyordu — sorgu yalnız RLS'e güveniyordu.
///
/// RLS'siz PLATFORM tabloları bilinçli istisnadır ve <see cref="RlsFreePlatformTables"/>
/// listesinde GEREKÇESİYLE durur. Listeye eklemek bir karardır, kaza değil.
/// </summary>
public sealed class TenantIsolationTests
{
    /// <summary>
    /// İşyeri sütunu taşıdığı halde RLS'siz olan tablolar ve NEDEN.
    ///
    /// Ortak gerekçe: bu kayıtlar işyeri bağlamı KURULMADAN önce okunur — işyerini
    /// bulmak için onlara bakılır (tavuk-yumurta). TenantId raporlama içindir.
    /// </summary>
    private static readonly Dictionary<Type, string> RlsFreePlatformTables = new()
    {
        [typeof(Modules.Tenancy.Domain.ApiKey)] =
            "İşyeri bu anahtarla ÇÖZÜLÜR — bakmadan önce işyeri bilinemez.",
        [typeof(Modules.Tenancy.Domain.UserTenant)] =
            "Kullanıcının hangi işyerlerinde üye olduğu, işyeri seçilmeden okunur.",
        [typeof(Modules.Tenancy.Domain.RefreshToken)] =
            "Belirteç yenileme işyeri bağlamı kurulmadan önce olur.",
        [typeof(Modules.Tenancy.Domain.EmailMessageRecord)] =
            "Parola sıfırlama postası kullanıcı giriş YAPAMAZKEN üretilir.",
        [typeof(Modules.Tenancy.Domain.SmsMessageRecord)] =
            "E-posta kuyruğuyla aynı desen ve aynı gerekçe.",
        [typeof(Modules.Payments.Domain.CallbackToken)] =
            "Banka dönüşünde API anahtarı yoktur; işyeri bu belirteçle çözülür.",
        [typeof(Modules.Payments.Domain.OutboxMessage)] =
            "Dağıtıcı işyeri döngüsünü kendisi kurar; süpürme işyeri bağlamı öncesidir.",
        [typeof(Modules.PaymentLinks.Domain.PaymentLinkLookup)] =
            "Checkout slug'ı işyeri bilinmeden çözer.",
    };

    /// <summary>
    /// Denetlenecek DbContext'ler YANSIMAYLA bulunur, elle sayılmaz.
    ///
    /// Elle yazılan liste bu bekçinin kör noktasıydı: yeni bir modül eklendiğinde
    /// listeye eklenmesi UNUTULUR ve modülün tabloları hiç denetlenmeden yayına çıkar —
    /// üstelik test yeşil yandığı için her şey yolunda görünür. Bekçinin kapsamı,
    /// koruduğu şeyle birlikte kendiliğinden büyümelidir.
    /// </summary>
    private static readonly Type[] DbContexts = Modules();

    private static Type[] Modules()
    {
        // AppDomain taraması YETMEZ: .NET assembly'leri tembel yükler, dolayısıyla henüz
        // dokunulmamış bir modül listede hiç görünmez ve test onu denetlemeden yeşil yanar.
        // Bunun yerine DİSKTEKİ modül projeleri gerçek kaynaktır ve her biri için assembly
        // çıktıda BULUNMAK ZORUNDADIR — yoksa test projesi o modülü referanslamamış demektir
        // ve modül sessizce denetim dışı kalmıştır.
        var root = FindRepositoryRoot();
        var expected = Directory.EnumerateDirectories(Path.Combine(root, "src", "Modules"))
            .Select(Path.GetFileName)
            .Where(name => name?.StartsWith("Poyra.Modules.", StringComparison.Ordinal) == true)
            .OrderBy(name => name)
            .ToList();

        expected.Count.ShouldBeGreaterThan(0, "src/Modules altında modül bulunamadı.");

        var contexts = new List<Type>();
        var missing = new List<string>();

        foreach (var name in expected)
        {
            var path = Path.Combine(AppContext.BaseDirectory, $"{name}.dll");
            if (!File.Exists(path))
            {
                missing.Add(name!);
                continue;
            }

            contexts.AddRange(Assembly.LoadFrom(path).GetTypes()
                .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(DbContext).IsAssignableFrom(t)));
        }

        missing.ShouldBeEmpty(
            "Bu modüllerin assembly'si test çıktısında yok — işyeri izolasyonu HİÇ denetlenmiyor. "
            + "Poyra.Tests.Architecture.csproj'a ProjectReference ekleyin: " + string.Join(", ", missing));

        return contexts.OrderBy(t => t.Name).ToArray();
    }

    private static IEnumerable<Type> EntityTypes()
        => DbContexts
            .SelectMany(context => context.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .Distinct();

    [Fact]
    public void Isyeri_verisi_tasiyan_her_varlik_ITenantOwned_olmali()
    {
        var offenders = EntityTypes()
            .Where(t => t.GetProperty("TenantId") is not null)
            .Where(t => !typeof(ITenantOwned).IsAssignableFrom(t))
            .Where(t => !RlsFreePlatformTables.ContainsKey(t))
            .Select(t => t.Name)
            .ToList();

        offenders.ShouldBeEmpty(
            "İşyeri sütunu taşıyan ama ITenantOwned uygulamayan varlık, EF global "
            + "filtresinden (Katman A) MUAFTIR ve yalnız RLS'e güvenir. Ya arayüzü "
            + "uygulayın ya da RlsFreePlatformTables listesine GEREKÇESİYLE ekleyin: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void RLS_siz_platform_tablolari_gerekceli_olmali()
    {
        // Liste bir KARAR kaydıdır: gerekçesiz giriş, "neden burada" sorusunu
        // altı ay sonra cevapsız bırakır
        foreach (var (type, reason) in RlsFreePlatformTables)
        {
            reason.ShouldNotBeNullOrWhiteSpace($"{type.Name} gerekçesiz.");
            reason.Length.ShouldBeGreaterThan(30, $"{type.Name} gerekçesi fazla kısa.");
        }
    }

    [Fact]
    public void RLS_siz_liste_gercekten_RLS_siz_olmali()
    {
        // Liste ile migration'lar birbirinden kaymamalı: bir tabloya sonradan RLS
        // eklendiyse istisna listesinden ÇIKARILMALI, yoksa liste yalan söyler
        var withPolicy = MigrationPolicyTables();

        foreach (var type in RlsFreePlatformTables.Keys)
        {
            var table = TableNameOf(type);
            withPolicy.ShouldNotContain(table,
                $"{type.Name} ({table}) RLS'siz sayılıyor ama migration'da tenant_isolation "
                + "politikası var — istisna listesinden çıkarılmalı.");
        }
    }

    /// <summary>
    /// <c>TenantId</c> sütunu OLMADIĞI için RLS beklenmeyen tablolar ve NEDEN.
    ///
    /// Yukarıdaki test bu tabloları göremez — kör noktası budur. Oysa asıl tehlikeli
    /// yön burasıdır: yeni bir modül eklenir, RLS migration'ı unutulur ve tablo sessizce
    /// korumasız yayına çıkar. Aşağıdaki test o yönü kapatır.
    /// </summary>
    private static readonly Dictionary<string, string> NonTenantTables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tenants"] =
            "İşyerinin KENDİSİ. Yalıtım id üzerindedir; her erişim yolu Id'ye göre filtreli "
            + "(GetMyTenant, marka adı, belirteç yenileme). Arka plan işleri (TenantDirectory) ve "
            + "checkout, işyeri bağlamı kurulmadan tüm işyerlerini taramak zorundadır.",
        ["organizations"] =
            "İşyeri üstü tüzel kişilik; işyeri sütunu yoktur, işyeri ona bağlıdır.",
        ["users"] =
            "Kullanıcı kimliği GLOBALdir — aynı kişi birden çok işyerinde üye olabilir. "
            + "İşyerine bağlanma noktası user_tenants'tır ve giriş öncesi okunur.",
        ["user_tokens"] =
            "ASP.NET Identity belirteç deposu; kullanıcıya bağlıdır, işyerine değil.",
        ["card_bins"] =
            "PLATFORM BIN kataloğu — tüm işyerleri aynı veriyi okur (ilk 6-8 hane, PCI dışı).",
        ["bank_holidays"] =
            "Resmî tatil takvimi platform geneli referans veridir; işyerine göre değişmez.",
    };

    [Fact]
    public void RLS_siz_kalan_her_tablo_belgelenmis_olmali()
    {
        // Ters yön: "listedeki tabloda politika olmasın" değil, "politikası olmayan her
        // tablo listede olsun". Gerçek kaza budur — yeni modülün RLS migration'ı unutulur,
        // hiçbir şey şikâyet etmez ve tablo korumasız yayına çıkar.
        var withRls = MigrationRlsTables();
        var documented = RlsFreePlatformTables.Keys.Select(TableNameOf)
            .Concat(NonTenantTables.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var undocumented = EntityTypes()
            .Select(TableNameOf)
            .Where(table => !withRls.Contains(table) && !documented.Contains(table))
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        undocumented.ShouldBeEmpty(
            "Bu tablolarda RLS (Katman B) YOK ve belgelenmiş bir istisna da değiller. "
            + "Ya migration'a 'ENABLE ROW LEVEL SECURITY' + tenant_isolation politikası ekleyin, "
            + "ya da gerekçesiyle RlsFreePlatformTables/NonTenantTables listesine yazın: "
            + string.Join(", ", undocumented));
    }

    [Fact]
    public void Isyeri_sutunu_olmayan_istisnalar_gerekceli_olmali()
    {
        foreach (var (table, reason) in NonTenantTables)
            reason.Length.ShouldBeGreaterThan(30, $"{table} gerekçesi fazla kısa.");
    }

    /// <summary>Migration'larda <c>ENABLE ROW LEVEL SECURITY</c> uygulanan tablolar.</summary>
    private static HashSet<string> MigrationRlsTables()
        => MigrationMatches(@"ALTER TABLE (\w+) ENABLE ROW LEVEL SECURITY");

    /// <summary>Migration'larda <c>CREATE POLICY tenant_isolation</c> uygulanan tablolar.</summary>
    private static HashSet<string> MigrationPolicyTables()
        => MigrationMatches(@"CREATE POLICY tenant_isolation ON (\w+)");

    private static HashSet<string> MigrationMatches(string pattern)
    {
        var root = FindRepositoryRoot();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "Modules"), "*.cs", SearchOption.AllDirectories))
        {
            if (!file.Contains("Migrations")) continue;

            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), pattern))
            {
                tables.Add(match.Groups[1].Value);
            }
        }

        return tables;
    }

    /// <summary>DbSet özelliği adından snake_case tablo adı (EF adlandırma sözleşmesi).</summary>
    private static string TableNameOf(Type entity)
    {
        var property = DbContexts
            .SelectMany(c => c.GetProperties())
            .First(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericArguments().FirstOrDefault() == entity);

        return System.Text.RegularExpressions.Regex
            .Replace(property.Name, "(?<!^)(?=[A-Z])", "_").ToLowerInvariant();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Poyra.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Poyra.slnx bulunamadı.");
    }
}

/// <summary>
/// Türkçe harf tuzağının bekçisi: kullanıcı yazımı metni EŞLEŞTİRMEK için
/// ham <c>ToLowerInvariant()</c> kullanmak, 'İ' harfini katlamaz ve sessiz bir
/// eşleşmeme doğurur (kullanıcı hesabına giremez, kara liste tutmaz).
///
/// Bu test kaynak tarar: e-posta/alan adı gibi kullanıcı metnini doğrudan
/// küçülten yeni bir satır eklenirse kırmızı yanar. Doğru yol
/// <see cref="TurkishText"/> yardımcısıdır.
/// </summary>
public sealed class TurkishCasingGuardTests
{
    /// <summary>Ham küçültmenin GÜVENLİ olduğu yerler ve nedeni.</summary>
    private static readonly (string Fragment, string Reason)[] Allowed =
    [
        ("Currency", "ISO 4217 kodu — üç ASCII harf."),
        ("currency", "ISO 4217 kodu — üç ASCII harf."),
        ("s.ToString()", "Enum adı — ASCII."),
        ("Status.ToString()", "Enum adı — ASCII."),
        ("Type.ToString()", "Enum adı — ASCII."),
        ("Action.ToString()", "Enum adı — ASCII."),
        ("kv.Key", "Konnektör alan adı — ASCII protokol anahtarı."),
        ("posted", "Banka hash'i — ASCII hex."),
        ("expected", "Banka hash'i — ASCII hex."),
        ("status", "Durum kodu — ASCII."),
        ("fact", "Kural sinyali adı — ASCII."),
        ("program", "Kart programı — ASCII (bonus/world/maximum)."),
        ("dto.Program", "Kart programı — ASCII."),
        ("dto.Brand", "Kart markası — ASCII."),
        ("dto.CardType", "Kart tipi — ASCII."),
        ("type", "Ekstre satır tipi — ASCII."),
        ("parts[1]", "Ekstre satır tipi — İ zaten açıkça katlanıyor."),
        ("Sms:Provider", "Yapılandırma anahtarı — ASCII."),
        ("d.Type.ToString()", "Enum adı — ASCII."),
        ("value.Trim()", "TurkishText'in kendi uygulaması."),
    ];

    [Fact]
    public void Kullanici_metni_ham_ToLowerInvariant_ile_katlanmamali()
    {
        var root = FindRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs",
                     SearchOption.AllDirectories))
        {
            if (file.Contains("Migrations") || file.EndsWith("TurkishText.cs")) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains(".ToLowerInvariant()") && !line.Contains(".ToUpperInvariant()"))
                    continue;

                // Yorum satırları tuzağı AÇIKLAR, uygulamaz
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                    continue;

                if (Allowed.Any(a => line.Contains(a.Fragment)))
                    continue;

                offenders.Add($"{Path.GetFileName(file)}:{i + 1} → {line.Trim()}");
            }
        }

        offenders.ShouldBeEmpty(
            "Kullanıcı yazımı metni eşleştirmek için ham küçültme kullanılamaz: .NET'te "
            + "\"İ\".ToLowerInvariant() harfi DEĞİŞTİRMEZ ve eşleşme sessizce kaçar. "
            + "TurkishText.Fold/NormalizeEmail kullanın ya da güvenliyse Allowed listesine "
            + "gerekçesiyle ekleyin:\n" + string.Join("\n", offenders));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Poyra.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Poyra.slnx bulunamadı.");
    }
}
