using Poyra.Modules.Ledger.Domain;
using Poyra.Modules.Ledger.Infrastructure;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// MT940 — SWIFT hesap ekstresi, TR kurumsal bankacılığın fiilî standardı.
///
/// Bu ayrıştırıcı üç yönlü mutabakatın girdi kapısıdır: yanlış okunan bir tutar,
/// olmayan bir "eksik yatırma" bulgusu üretir ve işyerine bankaya haksız itiraz
/// yazdırır. Bu yüzden ayrıştırıcı <b>anlamadığını sessizce atlamaz</b>, hata bildirir.
/// </summary>
public sealed class Mt940ParserTests
{
    private static SettlementParseResult Parse(string content)
        => new Mt940SettlementParser().Parse(new StringReader(content));

    [Fact]
    public void Alacak_hareketi_POZITIF_okunmali()
    {
        var result = Parse(
            """
            :61:2608050805C45230,18NTRFNONREF//GRT12345
            :86:GARANTI POS HASILAT 05/08
            """);

        result.Errors.ShouldBeEmpty();
        var line = result.Lines.ShouldHaveSingleItem();

        line.ValueDate.ShouldBe(new DateOnly(2026, 8, 5));
        line.AmountMinor.ShouldBe(4_523_018);          // 45.230,18 ₺
        line.Description.ShouldBe("GARANTI POS HASILAT 05/08");
        line.Reference.ShouldBe("GRT12345");
    }

    [Fact]
    public void Borc_hareketi_NEGATIF_okunmali()
    {
        // Banka hasılattan iade/komisyon mahsup ettiğinde borç satırı gelir.
        // İşareti karıştırmak, eksik yatan parayı fazla yatmış gibi gösterirdi.
        var result = Parse(":61:2608060806D15000,00NTRFNONREF//IADE\n");

        result.Lines.ShouldHaveSingleItem().AmountMinor.ShouldBe(-1_500_000);
    }

    [Fact]
    public void Ters_alacak_kaydi_da_NEGATIF_olmali()
    {
        // RC = reversal of credit — yatan paranın geri alınması
        var result = Parse(":61:2608070807RC5000,00NTRFNONREF\n");

        result.Lines.ShouldHaveSingleItem().AmountMinor.ShouldBe(-500_000);
    }

    [Fact]
    public void Ondalik_VIRGUL_binlik_NOKTA_dogru_okunmali()
    {
        // MT940 ISO standardıdır: ondalık ayracı VİRGÜL. Nokta binlik olabilir.
        // İkisini karıştırmak bin kat hata demektir.
        var result = Parse(":61:2608050805C1.234.567,89NTRFNONREF\n");

        result.Lines.ShouldHaveSingleItem().AmountMinor.ShouldBe(123_456_789);
    }

    [Fact]
    public void Kayit_tarihi_OLMAYAN_satir_da_okunmali()
    {
        // Bazı bankalar MMDD kayıt tarihini yazmaz — satır yine geçerlidir
        var result = Parse(":61:260805C1000,00NTRFNONREF\n");

        result.Errors.ShouldBeEmpty();
        result.Lines.ShouldHaveSingleItem().ValueDate.ShouldBe(new DateOnly(2026, 8, 5));
    }

    [Fact]
    public void Coklu_hareket_ve_aciklama_dogru_eslesmeli()
    {
        var result = Parse(
            """
            :20:EKSTRE001
            :25:TR330006100519786457841326
            :61:2608050805C10000,00NTRFNONREF//A1
            :86:POS HASILAT
            :61:2608060806C20000,00NTRFNONREF//A2
            :86:POS HASILAT 2
            :62F:C260806TRY30000,00
            """);

        result.Errors.ShouldBeEmpty();
        result.Lines.Count.ShouldBe(2);

        // Açıklama ÖNCEKİ hareketi anlatır — kaydırılırsa fark ararken yanlış
        // satıra bakılır
        result.Lines[0].Reference.ShouldBe("A1");
        result.Lines[0].Description.ShouldBe("POS HASILAT");
        result.Lines[1].Reference.ShouldBe("A2");
        result.Lines[1].Description.ShouldBe("POS HASILAT 2");
    }

    [Fact]
    public void Aciklamasiz_son_hareket_KAYBOLMAMALI()
    {
        // Dosya :86: olmadan biterse son hareket yazılmadan düşerdi — o hareket
        // günün hasılatı olabilir ve kaybı "para yatmamış" bulgusu üretirdi
        var result = Parse(":61:2608050805C10000,00NTRFNONREF\n");

        result.Lines.ShouldHaveSingleItem().AmountMinor.ShouldBe(1_000_000);
    }

    [Fact]
    public void Cozulemeyen_satir_SESSIZCE_ATLANMAMALI()
    {
        // Banka biçimi farklıysa bunu bilmemiz gerekir: eksik okunan bir ekstre,
        // olmayan bir "eksik yatırma" bulgusu üretir
        var result = Parse(":61:BOZUKVERI\n");

        result.Errors.ShouldNotBeEmpty();
        result.Errors[0].ShouldContain("banka biçimi");
        result.Lines.ShouldBeEmpty();
    }

    [Fact]
    public void Gecersiz_tarih_hata_vermeli()
    {
        var result = Parse(":61:2613320805C1000,00NTRFNONREF\n"); // 13. ay

        result.Errors.ShouldNotBeEmpty();
        result.Lines.ShouldBeEmpty();
    }

    [Fact]
    public void Yuzyil_yakin_tarihe_gore_cozulmeli()
    {
        // "26" → 2026, "95" → 1995. Ekstreler güncel olduğu için bu varsayım güvenli
        Parse(":61:260805C100,00N\n").Lines[0].ValueDate.Year.ShouldBe(2026);
        Parse(":61:950805C100,00N\n").Lines[0].ValueDate.Year.ShouldBe(1995);
    }
}

/// <summary>
/// Gün-küme karşılaştırmasının sınıflandırması. Burada bir hata, işyerinin
/// bankadan alacağını "tahsil edildi" diye gömer ya da olmayan bir eksik uydurur.
/// </summary>
public sealed class SettlementClassifyTests
{
    [Fact]
    public void Kurusu_kurusuna_yatan_gun_SETTLED_olmali()
        => SettlementFinding.Classify(4_523_018, 4_523_018).ShouldBe(SettlementOutcome.Settled);

    [Fact]
    public void Eksik_yatan_gun_SHORT_olmali()
        => SettlementFinding.Classify(1_240_000, 1_190_000).ShouldBe(SettlementOutcome.Short);

    [Fact]
    public void Hic_yatmayan_gun_MISSING_olmali()
        => SettlementFinding.Classify(875_000, 0).ShouldBe(SettlementOutcome.Missing);

    [Fact]
    public void Fazla_yatan_gun_OVER_olmali()
        => SettlementFinding.Classify(100_000, 150_000).ShouldBe(SettlementOutcome.Over);

    [Fact]
    public void TEK_KURUS_fark_bile_SHORT_sayilmali()
    {
        // Tolerans YOKTUR ve bu bilinçlidir: "birkaç kuruş önemsizdir" demek,
        // ürünün varlık sebebini inkâr etmektir. Sistematik bir kuruş farkı
        // milyonlarca işlemde gerçek paradır ve tolerans onu görünmez kılar.
        SettlementFinding.Classify(1_000_000, 999_999).ShouldBe(SettlementOutcome.Short);
        SettlementFinding.Classify(1_000_000, 1_000_001).ShouldBe(SettlementOutcome.Over);
    }

    [Fact]
    public void Beklenen_de_gercek_de_sifirsa_SETTLED_olmali()
    {
        // O gün ne alacak vardı ne para yattı — sorun yok
        SettlementFinding.Classify(0, 0).ShouldBe(SettlementOutcome.Settled);
    }
}
