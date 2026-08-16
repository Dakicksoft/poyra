using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Connectors;
using Poyra.Modules.Connectors.Infrastructure;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Konnektör uyum kiti: her <see cref="IPaymentConnector"/> uygulamasının tutmak
/// ZORUNDA olduğu sözleşme. Konnektörlerin tek tek testleri (GvpHashTests, PosnetMacTests…)
/// her bankanın kendine özgü hash'ini doğrular; burası ortak davranışı doğrular.
///
/// Kapsam ölçümü bu katmana ihtiyacı gösterdi: yerli banka konnektörleri kod tabanının
/// en az kapsanan yeriydi (NestPay %19,2 · Gvp %23,8 · Posnet %25 — Stripe %89'a karşılık).
/// Hash'ler testliydi ama sözleşme değildi.
///
/// Konnektör listesi DI kaydından okunur: yeni bir banka eklendiğinde bu teorilerin
/// verisi kendiliğinden büyür, kimsenin test eklemesi gerekmez.
/// </summary>
public sealed class KonnektorUyumTests
{
    public static TheoryData<string> Konnektorler { get; } = [.. KayitliAnahtarlar()];

    // ---- Katalog bütünlüğü -----------------------------------------------------

    [Fact]
    public void Kayitli_her_konnektor_katalogda_gorunmeli()
    {
        using var saglayici = Kur();
        var katalog = saglayici.GetRequiredService<ConnectorRegistry>()
            .Catalog().Select(d => d.Key);

        // Katalog, panelin "POS bağlantısı ekle" listesini besler. Kayıtlı ama katalogda
        // olmayan bir konnektör kodda vardır, testleri geçer, sahada YOKTUR: işyeri onu
        // panelden seçemez. Sessiz kaybolma biçimi budur.
        katalog.ShouldBe(KayitliAnahtarlar(), ignoreOrder: true);
    }

    // ---- Tanımlayıcı ------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Konnektorler))]
    public void Tanimlayici_tutarli_olmali(string anahtar)
    {
        var konnektor = Konnektor(anahtar);
        var tanim = konnektor.Descriptor;

        konnektor.Key.ShouldBe(anahtar);
        tanim.Key.ShouldBe(konnektor.Key); // kayıt anahtarı ile tanımlayıcı ayrışamaz
        anahtar.ShouldBe(anahtar.ToLowerInvariant());
        anahtar.ShouldNotContain(" ");
        tanim.DisplayName.ShouldNotBeNullOrWhiteSpace();

        tanim.CredentialFields.ShouldNotBeEmpty();
        tanim.CredentialFields.Select(a => a.Name).ShouldBeUnique();
        tanim.CredentialFields.ShouldContain(a => a.Required);
        foreach (var alan in tanim.CredentialFields)
        {
            alan.Name.ShouldBe(alan.Name.ToLowerInvariant());
            alan.Label.ShouldNotBeNullOrWhiteSpace(); // panelde bu metin gösterilir
        }
    }

    // ---- Kimlik doğrulama -------------------------------------------------------

    [Theory]
    [MemberData(nameof(Konnektorler))]
    public async Task Kimlik_alanlari_eksikse_yapilandirma_hatasi_atmali(string anahtar)
    {
        var konnektor = Konnektor(anahtar);
        var bos = new ConnectorCredentials(new Dictionary<string, string>());

        // Beklenen istisna TİPİ önemli: yapılandırma hatası işyerine "POS bilgilerinizi
        // tamamlayın" diye döner. NullReference/KeyNotFound sızarsa müşteri 500 görür.
        var hata = await Should.ThrowAsync<ConnectorConfigurationException>(
            () => konnektor.InitiateHostedPaymentAsync(OrnekIstek(), bos, default));

        hata.Message.ShouldNotBeNullOrWhiteSpace();
    }

    // ---- Callback sağlamlığı ----------------------------------------------------

    [Theory]
    [MemberData(nameof(Konnektorler))]
    public void Bos_ya_da_kurcalanmis_callback_kabul_edilmemeli(string anahtar)
    {
        var konnektor = Konnektor(anahtar);
        var kimlik = OrnekKimlik(konnektor);

        Dictionary<string, string>[] formlar =
        [
            new(),
            new() { ["mb_order"] = "att_1", ["mb_outcome"] = "approved", ["mb_sig"] = "sahte" },

            // "Onaylandı" görünen ama İMZASIZ form. Türk sanal POS protokollerinin
            // onay işaretleri bir arada: konnektörlerden biri imzayı atlayıp bunlardan
            // birine bakıyorsa, callback adresini bilen herkes bedava tahsilat yaratır.
            new()
            {
                ["OrderId"] = "att_uyum_0001",
                ["MerchantOrderId"] = "att_uyum_0001",
                ["Response"] = "Approved",
                ["ResponseCode"] = "00",
                ["ProcReturnCode"] = "00",
                ["Status"] = "Approved",
                ["mdStatus"] = "1",
                ["AuthCode"] = "123456",
            },
        ];

        foreach (var form in formlar)
        {
            // Callback müşterinin TARAYICISINDAN gelir — yani saldırganın kontrolünde.
            // İmzasız bir "onaylandı" formu asla başarı sayılmamalı ve istisna da atmamalı
            // (atarsa 500 döner, gerçek dönüşle ayırt edilemez).
            var sonuc = konnektor.ParseAndValidateCallback(form, kimlik);

            sonuc.Success.ShouldBeFalse($"{anahtar}: imzasız form kabul edildi");

            // Hangi kodu seçtiği konnektöre kalmış (imza mı bozuk, 3DS mi düşmüş — banka
            // protokolüne göre değişir), ama SÖZLÜKTE olmak zorunda: DunningPolicy,
            // failover kararı ve panel mesaj sözlüğü bu kodlara bakıyor. Sözlük dışı bir
            // dize sessizce "bilinmeyen hata" davranışına düşer.
            BilinenKodlar.ShouldContain(sonuc.UnifiedCode,
                $"{anahtar}: '{sonuc.UnifiedCode}' UnifiedErrors'ta tanımlı değil");
        }
    }

    /// <summary>UnifiedErrors'taki tüm sabitler — yeni kod eklendiğinde kendiliğinden büyür.</summary>
    private static readonly IReadOnlyList<string> BilinenKodlar =
    [
        .. typeof(UnifiedErrors)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(a => a is { IsLiteral: true, IsInitOnly: false } && a.FieldType == typeof(string))
            .Select(a => (string)a.GetRawConstantValue()!)
    ];

    // ---- Banka erişilemezliği ---------------------------------------------------

    [Theory]
    [MemberData(nameof(Konnektorler))]
    public async Task Banka_ulasilamazsa_failover_edilebilir_hata_dogmali(string anahtar)
    {
        using var saglayici = Kur(HttpStatusCode.ServiceUnavailable);
        var konnektor = Konnektor(anahtar, saglayici);

        try
        {
            // Form üretmek için sunucuya çıkmayan konnektörler (çoğu yerli banka)
            // buradan sorunsuz döner — beklenen davranış budur.
            var form = await konnektor.InitiateHostedPaymentAsync(
                OrnekIstek(), OrnekKimlik(konnektor), default);
            form.ShouldNotBeNull();
        }
        catch (ConnectorConfigurationException)
        {
            // Posnet gibi yalnız 3DS'li direct (OOS) destekleyen konnektörler hosted
            // çağrısında bilerek bunu atar. Yetenek ConnectorDescriptor'da BİLDİRİLMEDİĞİ
            // için kit bunu ancak istisnadan anlayabiliyor — tasarım notu docs'ta.
        }
        catch (ConnectorUnavailableException)
        {
            // Sunucuya çıkan konnektörler bu istisnayı atmalı: rota katmanı yalnız
            // bunu failover'a uygun sayar (UnifiedErrors.IsRetryableAtInitiate).
            // Ham HttpRequestException sızarsa ödeme failover yerine düşer.
        }
    }

    // ---- Sır sızıntısı ----------------------------------------------------------

    [Theory]
    [MemberData(nameof(Konnektorler))]
    public async Task Tarayiciya_giden_formda_sir_bulunmamali(string anahtar)
    {
        using var saglayici = Kur();
        var konnektor = Konnektor(anahtar, saglayici);
        var kimlik = OrnekKimlik(konnektor);

        HostedPaymentForm form;
        try
        {
            form = await konnektor.InitiateHostedPaymentAsync(OrnekIstek(), kimlik, default);
        }
        catch (ConnectorConfigurationException)
        {
            return; // hosted akışı hiç desteklemiyor (Posnet: yalnız 3DS'li direct/OOS)
        }
        catch (ConnectorUnavailableException)
        {
            return; // sunucuya çıkan konnektör; sahte banka yanıtı yok — bu test kapsam dışı
        }

        var sirlar = konnektor.Descriptor.CredentialFields
            .Where(a => a.Secret)
            .Select(a => kimlik.Get(a.Name))
            .Where(d => !string.IsNullOrEmpty(d))
            .ToList();

        // Form müşterinin tarayıcısında görünür. Sır oradan okunursa saldırgan kendi
        // imzasını üretip istediği tutarı "onaylandı" olarak geri gönderebilir.
        // Sırdan TÜRETİLEN hash'ler serbesttir — aranan şey düz metnin kendisi.
        foreach (var sir in sirlar)
        {
            form.ActionUrl.ShouldNotContain(sir!, Case.Insensitive);
            foreach (var (ad, deger) in form.Fields)
                deger.ShouldNotContain(sir!, Case.Insensitive, $"{anahtar}: '{ad}' alanı sırrı taşıyor");
        }

        form.ActionUrl.ShouldNotBeNullOrWhiteSpace();
        form.Fields.ShouldNotBeEmpty();
    }

    // ---- Yardımcılar ------------------------------------------------------------

    private static HostedPaymentRequest OrnekIstek() => new(
        OrderId: "att_uyum_0001",
        AmountMinor: 149_00,
        Currency: "TRY",
        Installments: 1,
        CallbackUrl: "https://api.poyra.test/v1/callbacks/uyum/belirteç",
        Description: "Uyum testi",
        CustomerIp: "203.0.113.7");

    /// <summary>
    /// Kimlik bilgileri TANIMLAYICIDAN üretilir — konnektöre özel bir sözlük tutulmaz,
    /// yeni banka kendi alanlarını bildirdiği anda örnek değerleri de oluşur.
    /// Sırlar aranabilir olsun diye ayırt edici bir kalıp taşır.
    /// </summary>
    private static ConnectorCredentials OrnekKimlik(IPaymentConnector konnektor)
        => new(konnektor.Descriptor.CredentialFields.ToDictionary(
            alan => alan.Name,
            alan => OrnekDeger(alan)));

    private static string OrnekDeger(CredentialField alan)
    {
        if (alan.Secret) return $"SIR-{alan.Name}-9F3A2B";
        if (alan.Name.Contains("base") || alan.Name.Contains("url")) return "https://banka.test";
        if (alan.Name.Contains("mode")) return "TEST";
        if (alan.Name.Contains("name") || alan.Name.Contains("user")) return "poyra_api";
        if (alan.Name.Contains("descriptor")) return "POYRA MAGAZA";
        if (alan.Name.Contains("key")) return "sk_test_uyum";
        return "10000001";
    }

    private static IPaymentConnector Konnektor(string anahtar, ServiceProvider? saglayici = null)
    {
        if (saglayici is not null)
            return saglayici.GetRequiredKeyedService<IPaymentConnector>(anahtar);

        using var yeni = Kur();
        return yeni.GetRequiredKeyedService<IPaymentConnector>(anahtar);
    }

    /// <summary>Gerçek DI kaydı + dışarı çıkmayan HTTP: hiçbir test bankaya bağlanmaz.</summary>
    private static ServiceProvider Kur(HttpStatusCode bankaYaniti = HttpStatusCode.ServiceUnavailable)
    {
        var services = new ServiceCollection();
        ConnectorsModule.Add(services);
        services.Configure<HttpClientFactoryOptions>(_ => { });
        services.ConfigureAll<HttpClientFactoryOptions>(secenekler =>
            secenekler.HttpMessageHandlerBuilderActions.Add(
                kurucu => kurucu.PrimaryHandler = new SabitYanitHandler(bankaYaniti)));
        return services.BuildServiceProvider();
    }

    private static IReadOnlyList<string> KayitliAnahtarlar()
    {
        var services = new ServiceCollection();
        ConnectorsModule.Add(services);
        return [.. services
            .Where(k => k.ServiceType == typeof(IPaymentConnector) && k.ServiceKey is string)
            .Select(k => (string)k.ServiceKey!)
            .Distinct()
            .OrderBy(a => a, StringComparer.Ordinal)];
    }

    private sealed class SabitYanitHandler(HttpStatusCode kod) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(kod)
            {
                Content = new StringContent("{}"),
                RequestMessage = request,
            });
    }
}
