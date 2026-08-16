using Poyra.Modules.Field.Domain;
using Poyra.SharedKernel.Time;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Saha tahsilatının çekirdek kuralları (M17). Hepsi İlke 2'nin farklı yüzleridir:
/// cihaz beyan eder, sunucu karar verir.
/// </summary>
public sealed class FieldDomainTests
{
    // ---------------------------------------------------------- cihaz ne üretebilir

    [Theory]
    [InlineData(FieldCollectionMethod.Link, FieldCollectionStatus.PendingRequest)]
    [InlineData(FieldCollectionMethod.Qr, FieldCollectionStatus.PendingRequest)]
    [InlineData(FieldCollectionMethod.SoftPosRedirect, FieldCollectionStatus.PendingRequest)]
    [InlineData(FieldCollectionMethod.CashDeclared, FieldCollectionStatus.CashDeclared)]
    public void Cihazin_uretebilecegi_baslangic_durumu(
        FieldCollectionMethod method, FieldCollectionStatus expected)
        => FieldCollection.InitialStatusFor(method).ShouldBe(expected);

    [Fact]
    public void Cihaz_HICBIR_yontemle_succeeded_uretemez()
    {
        // İLKE 2'nin özü: "tahsil edildi" cihazın söyleyebileceği bir şey değildir.
        // Bu test, ileride yeni bir yöntem eklenirse (ör. nfc) ve birileri onu
        // doğrudan Succeeded'a bağlarsa kırmızı yanar.
        foreach (var method in Enum.GetValues<FieldCollectionMethod>())
        {
            FieldCollection.InitialStatusFor(method)
                .ShouldNotBe(FieldCollectionStatus.Succeeded,
                    $"{method}: cihaz para durumunu üretemez, yalnız sunucu yazar.");
        }
    }

    [Fact]
    public void Nakit_beyani_succeeded_DEGILDIR()
    {
        // Nakit "tahsil edildi" gibi görünür ama Poyra'dan para GEÇMEZ. Succeeded
        // saymak, hiç görmediğimiz parayı mutabakata sokardı.
        FieldCollection.InitialStatusFor(FieldCollectionMethod.CashDeclared)
            .ShouldBe(FieldCollectionStatus.CashDeclared);
    }

    // ---------------------------------------------------------- cihaz saati

    [Fact]
    public void Cevrimdisi_gecen_sure_pozitif_sapma_verir()
    {
        var device = new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.FromHours(3));
        var server = device.AddHours(6); // temsilci 6 saat sonra ağa kavuştu

        FieldCollection.SkewSeconds(server, device).ShouldBe(6 * 3600);
    }

    [Fact]
    public void Ileri_alinmis_cihaz_saati_negatif_sapma_verir()
    {
        // Temsilci telefonun saatini ileri aldı: kayıt "gelecekte" alınmış görünür.
        // Reddetmiyoruz — sapma kaydediliyor ki rapor bunu gösterebilsin.
        var server = new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.FromHours(3));
        var device = server.AddDays(30);

        FieldCollection.SkewSeconds(server, device).ShouldBe(-30L * 86400);
    }

    [Fact]
    public void Fabrika_ayarina_donmus_cihaz_hesabi_PATLATMAMALI()
    {
        // Gerçek senaryo: pili biten ve fabrika ayarına dönen Android 1970'te açılır.
        // Naif bir (server - device) çıkarması TimeSpan taşmasına gidebilir; sonuç
        // yalnız büyük bir sayı olmalı, istisna DEĞİL.
        var server = new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);

        var epoch = FieldCollection.SkewSeconds(server, DateTimeOffset.UnixEpoch);
        epoch.ShouldBeGreaterThan(0);

        Should.NotThrow(() => FieldCollection.SkewSeconds(server, DateTimeOffset.MinValue));
        Should.NotThrow(() => FieldCollection.SkewSeconds(server, DateTimeOffset.MaxValue));
    }

    // ---------------------------------------------------------- eşleme bütünlüğü

    [Fact]
    public void Her_yontem_ve_durum_veritabani_karsiligina_sahip_olmali()
    {
        // Eşlemesi olmayan bir enum değeri, kaydı yazarken KeyNotFound ile patlar —
        // yani sahadaki tahsilat sunucuya hiç ulaşmaz. Yeni değer eklenince burası uyarır.
        foreach (var method in Enum.GetValues<FieldCollectionMethod>())
            FieldCollectionMethodMap.ToDb.ShouldContainKey(method);

        foreach (var status in Enum.GetValues<FieldCollectionStatus>())
            FieldCollectionStatusMap.ToDb.ShouldContainKey(status);

        FieldCollectionMethodMap.FromDb.Count.ShouldBe(FieldCollectionMethodMap.ToDb.Count);
        FieldCollectionStatusMap.FromDb.Count.ShouldBe(FieldCollectionStatusMap.ToDb.Count);
    }
}

/// <summary>
/// TR günü. Gün sonu saha özeti buna dayanır: 23:30'da (TR) alınan tahsilat UTC'de
/// ertesi güne düşer ve yanlış güne yazılırsa temsilcinin kasası tutmaz.
/// </summary>
public sealed class TurkeyTimeTests
{
    [Fact]
    public void Gece_yarisina_yakin_tahsilat_DOGRU_TR_gunune_dusmeli()
    {
        // 4 Ağustos 23:30 TR = 4 Ağustos 20:30 UTC. UTC gününe bakan bir hesap da
        // aynı sonucu verirdi; asıl ayrım bir sonraki testte.
        var moment = new DateTimeOffset(2026, 8, 4, 23, 30, 0, TimeSpan.FromHours(3));
        TurkeyTime.DayOf(moment).ShouldBe(new DateOnly(2026, 8, 4));
    }

    [Fact]
    public void UTC_gunu_ile_TR_gunu_ayristigi_yerde_TR_kazanmali()
    {
        // 5 Ağustos 01:00 TR = 4 Ağustos 22:00 UTC. Temsilci için bu 5 Ağustos'tur;
        // UTC'ye göre raporlamak tahsilatı bir önceki güne yazardı.
        var moment = new DateTimeOffset(2026, 8, 5, 1, 0, 0, TimeSpan.FromHours(3));

        moment.UtcDateTime.Day.ShouldBe(4);        // UTC 4 Ağustos diyor
        TurkeyTime.DayOf(moment).ShouldBe(new DateOnly(2026, 8, 5)); // TR 5 Ağustos
    }

    [Fact]
    public void Gun_araligi_YARI_ACIK_olmali()
    {
        var day = new DateOnly(2026, 8, 4);
        var start = TurkeyTime.StartOfDay(day);
        var end = TurkeyTime.EndOfDay(day);

        // Bitiş, ERTESİ günün başlangıcıdır. 23:59:59.999 kullanmak, son milisaniyede
        // alınan tahsilatın hiçbir günün raporuna girmemesi demekti.
        end.ShouldBe(TurkeyTime.StartOfDay(day.AddDays(1)));
        (end - start).ShouldBe(TimeSpan.FromHours(24));

        var lastMoment = new DateTimeOffset(2026, 8, 4, 23, 59, 59, 999, TimeSpan.FromHours(3));
        (lastMoment >= start && lastMoment < end).ShouldBeTrue();
    }

    [Fact]
    public void Sinirlar_Npgsql_icin_UTC_ofsetli_olmali()
    {
        // timestamptz yalnız UTC ofseti kabul eder; +03:00 ile sorgu çalışma
        // zamanında patlar (bu tuzağa daha önce düşüldü).
        TurkeyTime.StartOfDay(new DateOnly(2026, 8, 4)).Offset.ShouldBe(TimeSpan.Zero);
        TurkeyTime.EndOfDay(new DateOnly(2026, 8, 4)).Offset.ShouldBe(TimeSpan.Zero);
    }
}
