using Poyra.Modules.Ledger.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Bankadan alacak — farklılaşma tezinin çekirdeği.
///
/// Testlerin ortak sorusu: <b>"bilmiyoruz" ile "fark yok" karıştırılıyor mu?</b>
/// Karıştırılırsa işyeri bankaya haksız itiraz yazar ya da gerçek farkı kaçırır.
/// </summary>
public sealed class BankReceivableTests
{
    private static BankReceivable Sale(
        long gross = 100_000, int? rateBps = 250, DateOnly? expectedValue = null) => new()
    {
        AttemptPublicId = "att_x",
        GrossMinor = gross,
        Installments = 1,
        ExpectedRateBps = rateBps,
        ExpectedCommissionMinor = rateBps is { } bps ? BankReceivable.CommissionOf(gross, bps) : null,
        ExpectedValueDate = expectedValue,
        CapturedAtServer = DateTimeOffset.UtcNow,
    };

    // ---------------------------------------------------------------- komisyon

    [Theory]
    [InlineData(100_000, 250, 2_500)]   // 1.000,00 ₺ · %2,50 → 25,00 ₺
    [InlineData(14_990, 189, 283)]      // 149,90 ₺ · %1,89 → 2,83 ₺
    [InlineData(1, 250, 0)]             // 1 kuruş · %2,50 → 0 (yuvarlanır)
    public void Komisyon_anlasma_oranindan_hesaplanmali(long gross, int bps, long expected)
        => BankReceivable.CommissionOf(gross, bps).ShouldBe(expected);

    [Fact]
    public void Yuvarlama_BANKACI_yuvarlamasi_olmali()
    {
        // Tam yarımda ÇİFT sayıya yuvarlanır. Tek yönde yuvarlamak (hep yukarı),
        // çok sayıda işlemde işyerinin aleyhine sistematik olarak birikir.
        // 20.000 kuruş × %0,25 = 50,0 kuruş → 50 (çift)
        BankReceivable.CommissionOf(20_000, 25).ShouldBe(50);
        // 60.000 × %0,25 = 150,0 → 150 (çift)
        BankReceivable.CommissionOf(60_000, 25).ShouldBe(150);
    }

    // ---------------------------------------------------------------- "bilmiyoruz" ≠ "fark yok"

    [Fact]
    public void Anlasma_YOKSA_beklenen_tutar_null_olmali()
    {
        // Uydurulmuş bir oran, olmayan bir farkı varmış gibi gösterir ve işyerine
        // bankaya haksız itiraz yazdırır. "Bilmiyoruz" demek, yanlış bilmekten iyidir.
        var receivable = Sale(rateBps: null);

        receivable.ExpectedCommissionMinor.ShouldBeNull();
        receivable.CommissionDeltaMinor.ShouldBeNull();
    }

    [Fact]
    public void Teyit_YOKSA_fark_null_olmali_SIFIR_degil()
    {
        // Sıfır "banka doğru kesmiş" demektir; null "banka henüz konuşmadı".
        // İkisini karıştırmak, denetlenmemiş bir alacağı temiz göstermek olur.
        var receivable = Sale();

        receivable.ExpectedCommissionMinor.ShouldBe(2_500);
        receivable.CommissionDeltaMinor.ShouldBeNull();
    }

    [Fact]
    public void Banka_FAZLA_keserse_fark_POZITIF_olmali()
    {
        var receivable = Sale();
        receivable.ConfirmedCommissionMinor = 3_100; // anlaşma 2.500 idi

        receivable.CommissionDeltaMinor.ShouldBe(600); // 6,00 ₺ fazla kesilmiş
    }

    [Fact]
    public void Banka_EKSIK_keserse_fark_NEGATIF_olmali()
    {
        var receivable = Sale();
        receivable.ConfirmedCommissionMinor = 2_000;

        receivable.CommissionDeltaMinor.ShouldBe(-500);
    }

    // ---------------------------------------------------------------- gecikme

    [Fact]
    public void Valoru_gecmis_ve_TEYITSIZ_alacak_gecikmis_sayilmali()
    {
        var today = new DateOnly(2026, 8, 10);
        var receivable = Sale(expectedValue: new DateOnly(2026, 8, 7));

        receivable.IsOverdue(today).ShouldBeTrue();
        receivable.ValueDateDelayDays(today).ShouldBe(3);
    }

    [Fact]
    public void TEYIT_EDILMIS_alacak_gecikmis_SAYILMAMALI()
    {
        // Banka geç de olsa borcu kabul etti. "Kabul etti ama ödemedi" AYRI bir
        // sorundur ve onu hesap ekstresi mutabakatı yakalar — burada karıştırılırsa
        // iki farklı problem tek sayıya gömülür.
        var today = new DateOnly(2026, 8, 10);
        var receivable = Sale(expectedValue: new DateOnly(2026, 8, 7));
        receivable.Status = ReceivableStatus.BankConfirmed;

        receivable.IsOverdue(today).ShouldBeFalse();
    }

    [Fact]
    public void Valor_bilinmiyorsa_gecikme_hesaplanmamali()
    {
        var receivable = Sale(rateBps: null); // anlaşma yok → valör de yok

        receivable.IsOverdue(new DateOnly(2026, 8, 10)).ShouldBeFalse();
        receivable.ValueDateDelayDays(new DateOnly(2026, 8, 10)).ShouldBeNull();
    }

    [Fact]
    public void Erken_odeme_NEGATIF_gecikme_vermeli()
    {
        var receivable = Sale(expectedValue: new DateOnly(2026, 8, 10));
        receivable.ConfirmedValueDate = new DateOnly(2026, 8, 8);

        receivable.ValueDateDelayDays(new DateOnly(2026, 8, 20)).ShouldBe(-2);
    }

    [Fact]
    public void Teyit_varsa_gecikme_BUGUNE_degil_GERCEK_tarihe_gore_olmali()
    {
        // Banka 2 gün geç ödedi. Bugün 20'si olsa bile gecikme 2 gündür, 10 değil —
        // yoksa kapanmış bir gecikme her gün büyüyor görünürdü.
        var receivable = Sale(expectedValue: new DateOnly(2026, 8, 7));
        receivable.ConfirmedValueDate = new DateOnly(2026, 8, 9);

        receivable.ValueDateDelayDays(new DateOnly(2026, 8, 20)).ShouldBe(2);
    }

    // ---------------------------------------------------------------- eşleme bütünlüğü

    [Fact]
    public void Her_tur_ve_durumun_veritabani_karsiligi_olmali()
    {
        foreach (var kind in Enum.GetValues<ReceivableKind>())
            ReceivableKindMap.ToDb.ShouldContainKey(kind);

        foreach (var status in Enum.GetValues<ReceivableStatus>())
            ReceivableStatusMap.ToDb.ShouldContainKey(status);
    }
}

/// <summary>
/// Valör gecikmesinin parasal karşılığı. "Banka 3 gün geç ödedi" bir şikâyettir;
/// "3 gün geç ödediği için şu kadar finansman maliyetine girdiniz" bir karardır.
/// </summary>
public sealed class ValueDateCostTests
{
    [Fact]
    public void Gecikme_maliyeti_gun_orantili_olmali()
    {
        // 100.000,00 ₺ · %35 yıllık · 3 gün
        // 10.000.000 × 0,35 / 365 × 3 = 28.767,12 kuruş → 28.767
        ValueDateCost.Of(10_000_000, 3, 3_500).ShouldBe(28_767);
    }

    [Fact]
    public void Gecikme_iki_katina_cikinca_maliyet_de_iki_katina_cikmali()
    {
        var bir = ValueDateCost.Of(10_000_000, 5, 3_500);
        var iki = ValueDateCost.Of(10_000_000, 10, 3_500);

        // Yuvarlama nedeniyle tam iki kat olmayabilir; 1 kuruş tolerans
        Math.Abs(iki - bir * 2).ShouldBeLessThanOrEqualTo(1);
    }

    [Theory]
    [InlineData(0)]     // gecikme yok
    [InlineData(-3)]    // erken ödeme
    public void Gecikme_yoksa_maliyet_SIFIR_olmali(int delayDays)
        => ValueDateCost.Of(10_000_000, delayDays, 3_500).ShouldBe(0);

    [Fact]
    public void Oran_tanimsizsa_maliyet_hesaplanmamali()
    {
        // Uydurulmuş bir oran, olmayan bir zararı varmış gibi gösterir
        ValueDateCost.Of(10_000_000, 5, 0).ShouldBe(0);
    }

    [Fact]
    public void Gun_sayaci_365_olmali()
    {
        // TRY'de bankacılık pratiği ACT/365. 360 kullanmak maliyeti %1,4 abartır ve
        // bankayla tartışma rakamda değil YÖNTEMDE çıkar.
        ValueDateCost.DaysInYear.ShouldBe(365);

        // Bir yıllık gecikme, yıllık oranın tamamı kadar maliyet üretmeli
        ValueDateCost.Of(1_000_000, 365, 3_500).ShouldBe(350_000); // %35
    }

    [Fact]
    public void Negatif_tutar_maliyet_uretmemeli()
    {
        // İade alacağı negatiftir; gecikmesi "kazanç" gibi görünmemeli
        ValueDateCost.Of(-10_000, 5, 3_500).ShouldBe(0);
    }
}
