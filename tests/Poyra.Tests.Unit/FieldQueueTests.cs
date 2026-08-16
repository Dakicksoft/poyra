using System.Net;
using Poyra.Field.Core;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Sahadaki cihazın çevrimdışı kuyruğu (M17 istemci çekirdeği).
///
/// Her testin sorduğu tek soru: <b>ağ koptuğunda para kaybolur mu?</b> Kaybolmamalı.
/// Kapsama alanı dışında kalmak sahanın normalidir, istisna değil — kuyruk buna göre
/// tasarlanmıştır.
/// </summary>
public sealed class FieldQueueTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"poyra-saha-{Guid.NewGuid():N}.db");
    private readonly FieldQueueDbContext _db;
    private readonly FieldQueue _queue;

    public FieldQueueTests()
    {
        _db = FieldQueueDbContext.Open(_dbPath);
        _queue = new FieldQueue(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        FieldQueueTests.DeleteQuietly(_dbPath);
    }

    /// <summary>
    /// Geçici dosyayı silmeye ÇALIŞIR, başaramazsa sessizce geçer.
    ///
    /// Burada bilerek <c>SqliteConnection.ClearAllPools()</c> ÇAĞRILMAZ: o çağrı
    /// KÜRESELdir ve xUnit test sınıflarını paralel koşturduğu için aynı anda çalışan
    /// başka bir sınıfın bağlantılarını da koparır. Kararsız (flaky) bir test, hata
    /// aramayı bilgi değil gürültü hâline getirir; havuz kilidi yüzünden silinemeyen
    /// birkaç geçici dosya ise zararsızdır — işletim sistemi temizler.
    /// </summary>
    internal static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Havuzdaki bağlantı dosyayı hâlâ tutuyor olabilir; sorun değil.
        }
    }

    private static QueuedCollection Item(long amount = 10_000, string method = "cash_declared")
        => new()
        {
            Method = method,
            AmountMinor = amount,
            CapturedAtDevice = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)),
        };

    // ---------------------------------------------------------------- kalıcılık

    [Fact]
    public async Task Kayit_AG_OLMADAN_tamamlanmali_ve_kalici_olmali()
    {
        // Temsilci kapsama alanı dışındaki bir dükkânda: tahsilat kaydı internet
        // beklemeden bitmeli, yoksa ürün sahada kullanılamaz.
        var saved = await _queue.EnqueueAsync(Item(45_000));
        saved.State.ShouldBe(QueueState.Pending);

        // Uygulama kapansa bile kayıt duruyor
        await using var reopened = FieldQueueDbContext.Open(_dbPath);
        var pending = await new FieldQueue(reopened).PendingAsync();

        pending.ShouldHaveSingleItem().AmountMinor.ShouldBe(45_000);
    }

    [Fact]
    public async Task Islem_kimligi_yeniden_denemede_DEGISMEMELI()
    {
        // Kuyruğun tüm güvenliği buna dayanır: kimlik her denemede yeniden üretilseydi
        // sunucu her denemeyi yeni bir tahsilat sanar ve müşteriye tekrar tekrar
        // ödeme talebi giderdi.
        var item = await _queue.EnqueueAsync(Item());
        var original = item.ClientOpId;

        for (var i = 0; i < 3; i++)
        {
            await _queue.MarkAttemptAsync([item.ClientOpId], DateTimeOffset.UtcNow);
            var again = (await _queue.PendingAsync()).ShouldHaveSingleItem();
            again.ClientOpId.ShouldBe(original);
        }

        (await _queue.PendingAsync()).Single().Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task Uretim_sirasi_KORUNMALI()
    {
        // Temsilcinin gün içindeki sırası, gün sonu kasa sayımında karşılığı olan
        // bir gerçektir; kuyruk onu bozmamalı.
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add((await _queue.EnqueueAsync(Item(1_000 * (i + 1)))).ClientOpId);

        (await _queue.PendingAsync()).Select(p => p.ClientOpId).ShouldBe(ids);
    }

    // ---------------------------------------------------------------- sonuç işleme

    [Fact]
    public async Task Kabul_ve_TEKRAR_ayni_sekilde_ele_alinmali()
    {
        var accepted = await _queue.EnqueueAsync(Item());
        var duplicate = await _queue.EnqueueAsync(Item());

        await _queue.ApplyAsync(
        [
            new SyncOutcome(accepted.ClientOpId, "accepted", "fc_1", "cash_declared", null, null),
            // 'duplicate' = "sunucuda zaten var". Ayrı ele alınsaydı, ağ koptuğu için
            // yeniden gönderilen kayıt sonsuza dek kuyrukta kalırdı.
            new SyncOutcome(duplicate.ClientOpId, "duplicate", "fc_2", "cash_declared", null, null),
        ]);

        (await _queue.PendingAsync()).ShouldBeEmpty();

        var rows = _db.Queue.ToList();
        rows.ShouldAllBe(r => r.State == QueueState.Synced);
        rows.Select(r => r.ServerId).ShouldBe(new[] { "fc_1", "fc_2" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Ret_KALICI_olmali_ve_kuyrugu_tikamamali()
    {
        var good = await _queue.EnqueueAsync(Item(5_000));
        var bad = await _queue.EnqueueAsync(Item(-1));

        await _queue.ApplyAsync(
        [
            new SyncOutcome(good.ClientOpId, "accepted", "fc_1", "cash_declared", null, null),
            new SyncOutcome(bad.ClientOpId, "rejected", null, null, null, "Tutar sıfırdan büyük olmalıdır."),
        ]);

        // Reddedilen yeniden GÖNDERİLMEZ — sonsuz döngü olurdu
        (await _queue.PendingAsync()).ShouldBeEmpty();

        // Ama SİLİNMEZ: temsilci neyin neden gitmediğini görebilmeli
        var rejected = (await _queue.RejectedAsync()).ShouldHaveSingleItem();
        rejected.ClientOpId.ShouldBe(bad.ClientOpId);
        rejected.RejectReason.ShouldContain("Tutar");
    }

    [Fact]
    public async Task Bilinmeyen_sonuc_kaydi_KAYBETMEMELI()
    {
        // Sunucu ileride yeni bir sonuç türü eklerse, eski sürümdeki cihaz kaydı
        // sessizce düşürmemeli — anlamadığını beklemeli.
        var item = await _queue.EnqueueAsync(Item());

        await _queue.ApplyAsync([new SyncOutcome(item.ClientOpId, "yeni_bir_sey", null, null, null, null)]);

        (await _queue.PendingAsync()).ShouldHaveSingleItem().ClientOpId.ShouldBe(item.ClientOpId);
    }

    [Fact]
    public async Task Parti_sinirini_asmamali()
    {
        for (var i = 0; i < FieldQueue.MaxBatch + 25; i++)
            await _queue.EnqueueAsync(Item(100));

        // Sunucudaki sınırla aynı: aşan bir parti tümüyle reddedilir ve kuyruk hiç ilerlemez
        (await _queue.PendingAsync()).Count.ShouldBe(FieldQueue.MaxBatch);
        (await _queue.PendingAsync(limit: 1000)).Count.ShouldBe(FieldQueue.MaxBatch);
    }
}

/// <summary>
/// Senkron istemcisi — ağın gerçekten koptuğu koşullar.
/// </summary>
public sealed class FieldSyncClientTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"poyra-senkron-{Guid.NewGuid():N}.db");
    private readonly FieldQueueDbContext _db;
    private readonly FieldQueue _queue;

    public FieldSyncClientTests()
    {
        _db = FieldQueueDbContext.Open(_dbPath);
        _queue = new FieldQueue(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        FieldQueueTests.DeleteQuietly(_dbPath);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private FieldSyncClient Client(StubHandler handler, DateTimeOffset? deviceNow = null)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://sunucu") },
            _queue, () => deviceNow ?? DateTimeOffset.UtcNow);

    private static QueuedCollection Item(long amount = 10_000) => new()
    {
        Method = "cash_declared",
        AmountMinor = amount,
        CapturedAtDevice = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)),
    };

    [Fact]
    public async Task Ag_yokken_kuyruk_OLDUGU_GIBI_kalmali()
    {
        await _queue.EnqueueAsync(Item(80_000));
        var handler = new StubHandler(_ => throw new HttpRequestException("Ağ yok"));

        var result = await Client(handler).RunAsync("bayi-01", "cihaz-A");

        result.Reachable.ShouldBeFalse();
        result.Accepted.ShouldBe(0);

        // Kayıt DURUYOR — kapsama alanı dışında olmak veri kaybı sebebi olamaz
        var pending = (await _queue.PendingAsync()).ShouldHaveSingleItem();
        pending.AmountMinor.ShouldBe(80_000);
        pending.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Sunucu_HATA_dondurse_bile_kayit_silinmemeli()
    {
        await _queue.EnqueueAsync(Item());
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"code":"internal_error"}"""),
        });

        var result = await Client(handler).RunAsync("bayi-01", "cihaz-A");

        result.Reachable.ShouldBeTrue();
        result.Error.ShouldContain("500");

        // Yanlış bir sunucu hatası yüzünden günün tahsilatını silmek, beklemekten
        // çok daha pahalıdır
        (await _queue.PendingAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Basarili_senkrondan_sonra_ayni_kayit_TEKRAR_gonderilmemeli()
    {
        var item = await _queue.EnqueueAsync(Item());
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {"serverTime":"2026-08-04T06:00:00+00:00","agentId":"{{Guid.Empty}}",
                 "accepted":1,"duplicate":0,"rejected":0,
                 "results":[{"clientOpId":"{{item.ClientOpId}}","outcome":"accepted",
                             "collectionId":"fc_1","status":"cash_declared",
                             "checkoutUrl":null,"reason":null}]}
                """, System.Text.Encoding.UTF8, "application/json"),
        });

        var client = Client(handler);
        var first = await client.RunAsync("bayi-01", "cihaz-A");
        first.Accepted.ShouldBe(1);

        // İkinci tur: gönderilecek bir şey kalmadığı için istek bile ATILMAZ
        var second = await client.RunAsync("bayi-01", "cihaz-A");
        second.Sent.ShouldBe(0);
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task BEKLENMEYEN_hata_uygulamayi_OLDURMEMELI()
    {
        // GERÇEK ÇÖKME: taban adres yanlış yapılandırıldığı için HttpClient
        // InvalidOperationException attı ve saha uygulaması KAPANDI. Temsilci
        // müşteri karşısındayken kapanan bir uygulama, o günün tahsilatını
        // yapamaması demektir.
        await _queue.EnqueueAsync(Item(60_000));
        var handler = new StubHandler(_ => throw new InvalidOperationException("net_http_client_invalid_requesturi"));

        // İstisna sızarsa test zaten kırmızı yanar — asıl iddia budur
        var result = await Client(handler).RunAsync("bayi-01", "cihaz-A");

        // Hata YUTULMAZ, metin olarak görünür
        result.Reachable.ShouldBeFalse();
        result.Error.ShouldContain("InvalidOperationException");

        // Ve kayıt kuyrukta DURUR
        (await _queue.PendingAsync()).ShouldHaveSingleItem().AmountMinor.ShouldBe(60_000);
    }

    [Fact]
    public async Task Bozuk_yanit_govdesi_de_cokme_YAPMAMALI()
    {
        // Sunucu bir gün geçersiz JSON dönerse (vekil sunucu hata sayfası, kesinti
        // sayfası) cihaz bunu hata olarak göstermeli, kapanmamalı
        await _queue.EnqueueAsync(Item());
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>vekil sunucu hata sayfası</html>",
                System.Text.Encoding.UTF8, "application/json"),
        });

        var result = await Client(handler).RunAsync("bayi-01", "cihaz-A");

        result.Error.ShouldNotBeNull();
        (await _queue.PendingAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Cihaz_saati_sapmasi_sunucudan_OGRENILMELI()
    {
        await _queue.EnqueueAsync(Item());
        var serverTime = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        var deviceTime = serverTime.AddHours(-2); // cihaz 2 saat geride

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {"serverTime":"{{serverTime:O}}","agentId":"{{Guid.Empty}}",
                 "accepted":0,"duplicate":0,"rejected":0,"results":[]}
                """, System.Text.Encoding.UTF8, "application/json"),
        });

        var result = await Client(handler, deviceTime).RunAsync("bayi-01", "cihaz-A");

        // Cihaz sistem saatini DEĞİŞTİRMEZ (başka uygulamaları bozar) ama farkı bilir
        // ve temsilciyi uyarabilir
        result.ClockSkew.ShouldBe(TimeSpan.FromHours(2));
        (result.ClockSkew > FieldSyncClient.SkewWarning).ShouldBeTrue();
    }

    [Fact]
    public async Task Gonderilen_zaman_damgasi_TR_ofsetiyle_de_calismali()
    {
        // Gerçek cihaz +03:00 gönderir. Bu tuzağa sunucu tarafında düşüldü;
        // istemci de aynı yazımı üretiyor olmalı ki sözleşme gerçeği yansıtsın.
        await _queue.EnqueueAsync(Item());
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"serverTime":"2026-08-04T06:00:00+00:00","agentId":"00000000-0000-0000-0000-000000000000",
                 "accepted":0,"duplicate":0,"rejected":0,"results":[]}
                """, System.Text.Encoding.UTF8, "application/json"),
        });

        await Client(handler).RunAsync("bayi-01", "cihaz-A");

        handler.LastBody.ShouldNotBeNull();
        handler.LastBody.ShouldContain("+03:00");
    }
}

/// <summary>
/// Sahada girilen tutar. Buradaki bir hata doğrudan para kaybıdır ve fark edilmesi
/// gün sonu kasa sayımına kalır.
///
/// <b>Bu sınıf GERÇEK CİHAZDA bulunan bir hatayla yeniden yazıldı:</b> Android'in
/// sayısal klavyesi virgülü yutuyor, noktayı kabul ediyordu. TR okumasında nokta
/// binlik ayracı olduğu için "1234.50" yazan temsilcinin tahsilatı 123.450,00 ₺
/// kaydediliyordu — 100 kat sapma. Birim testleri bunu göremezdi çünkü metni
/// doğrudan besliyorlardı; ancak emülatörde uygulamayı kullanınca ortaya çıktı.
///
/// Çözüm ayracı tamamen kaldırmaktır: yalnız rakam, kuruş sağdan dolar.
/// Türkiye'deki her POS terminali böyle çalışır.
/// </summary>
public sealed class TurkishMoneyTests
{
    [Theory]
    [InlineData("123450", 123_450)]   // 1.234,50 ₺
    [InlineData("1", 1)]              // 1 kuruş
    [InlineData("50", 50)]            // 50 kuruş
    [InlineData("10000", 10_000)]     // 100,00 ₺
    [InlineData("  2500  ", 2_500)]   // kırpılır
    public void Rakam_girisi_kurusa_cevrilmeli(string input, long expected)
    {
        TurkishMoney.TryParseDigits(input, out var minor).ShouldBeTrue();
        minor.ShouldBe(expected);
    }

    [Theory]
    [InlineData("1234,50")]   // ayraçlı giriş KABUL EDİLMEZ — belirsizliğin kaynağı buydu
    [InlineData("1234.50")]
    [InlineData("1.234,50")]
    [InlineData("12a34")]     // yabancı karakter sessizce atılmaz
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-50")]
    [InlineData("0")]         // sıfır tahsilat diye bir şey yok
    [InlineData("9999999999999999999")] // long taşması → negatif tutar riski
    public void Ayrac_ve_gecersiz_giris_reddedilmeli(string? input)
        => TurkishMoney.TryParseDigits(input, out _).ShouldBeFalse();

    [Fact]
    public void Nokta_ARTIK_sessizce_100_kat_hata_uretemez()
    {
        // Gerçek senaryo: temsilci 1.234,50 ₺ tahsil etti, klavye virgül vermediği
        // için "1234.50" yazdı. Eski davranış: 123.450,00 ₺ kaydedilirdi.
        // Yeni davranış: giriş reddedilir ve temsilci uyarılır.
        TurkishMoney.TryParseDigits("1234.50", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(123_450, "1.234,50 ₺")]
    [InlineData(5, "0,05 ₺")]
    [InlineData(0, "0,00 ₺")]
    [InlineData(100_000_000, "1.000.000,00 ₺")]
    public void Gosterimde_kurus_ASLA_gizlenmemeli(long minor, string expected)
        => TurkishMoney.Format(minor).ShouldBe(expected);

    [Theory]
    [InlineData("1", "0,01 ₺")]
    [InlineData("1234", "12,34 ₺")]
    [InlineData("123450", "1.234,50 ₺")]
    [InlineData("", "0,00 ₺")]
    [InlineData("abc", "0,00 ₺")]
    public void Canli_onizleme_yazarken_dogru_karsiligi_gostermeli(string input, string expected)
        => TurkishMoney.Preview(input).ShouldBe(expected);

    [Fact]
    public void Yazip_okumak_tutari_DEGISTIRMEMELI()
    {
        // Temsilcinin ekranda görüp onayladığı tutar ile kaydedilen aynı olmalı
        foreach (var minor in new long[] { 1, 5, 99, 100, 12_345, 999_999, 100_000_000 })
        {
            TurkishMoney.Preview(minor.ToString()).ShouldBe(TurkishMoney.Format(minor));
        }
    }
}

/// <summary>
/// Kuyruk durumunun ekrandaki karşılığı. Emülatörde listede ham "Pending" göründü —
/// uygulamanın tamamı Türkçeyken saha temsilcisinden İngilizce okuması beklenemez.
/// </summary>
public sealed class QueueStateTextTests
{
    [Fact]
    public void Her_durumun_TURKCE_karsiligi_olmali()
    {
        foreach (var state in Enum.GetValues<QueueState>())
        {
            var text = QueueStateText.Of(state);
            text.ShouldNotBe(state.ToString(), $"{state} için Türkçe karşılık yok.");
            text.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Bekleyen_kayit_KAYBOLMADIGINI_soylemeli()
    {
        // Temsilcinin kapsama alanı dışındayken göreceği metin: "gönderilemedi" değil,
        // "bekliyor" — kaydın durduğunu bilmeli
        QueueStateText.Of(QueueState.Pending).ShouldContain("bekliyor");
    }
}
