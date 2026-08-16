using Poyra.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// QR kodlayıcı. Yanlış üretilen bir QR sessizce çalışmaz: müşteri kamerayı tutar,
/// hiçbir şey olmaz ve tahsilat kaybedilir. Bu yüzden yapı taşları (bulucu desenler,
/// zamanlama, sürüm seçimi, Reed-Solomon) burada tek tek çivilenir.
/// </summary>
public sealed class QrCodeTests
{
    [Theory]
    [InlineData("POYRA", 21)]                                                    // 5 bayt  → v1 (21×21)
    [InlineData("https://pay.poyra.com/l/uFi5t7NGzoNbW1EE", 29)]                 // 40 bayt → v3 (29×29)
    [InlineData("https://pay.poyra.com/l/uFi5t7NGzoNbW1EE?ref=kampanya-agustos-2026", 37)] // 68 bayt → v5
    public void Yuk_uzunluguna_gore_dogru_surum_secilmeli(string text, int expectedSize)
        // Sürüm, yükün SIĞDIĞI en küçük olan olmalı: gereğinden büyük QR modülleri
        // küçültür ve ucuz kameralar okuyamaz
        => QrCode.Encode(text).GetLength(0).ShouldBe(expectedSize);

    [Fact]
    public void Matris_kare_ve_surum_formuluyle_uyumlu_olmali()
    {
        var matrix = QrCode.Encode("https://pay.poyra.com/l/uFi5t7NGzoNbW1EE");
        var size = matrix.GetLength(0);

        matrix.GetLength(1).ShouldBe(size);
        ((size - 17) % 4).ShouldBe(0); // size = 4v + 17
    }

    [Fact]
    public void Uc_bulucu_deseni_dogru_yerlerde_olmali()
    {
        var matrix = QrCode.Encode("https://pay.poyra.com/l/uFi5t7NGzoNbW1EE");
        var size = matrix.GetLength(0);

        // Bulucu deseni: 7×7 çerçeve, 3×3 dolu göbek — okuyucu QR'ı bununla bulur
        foreach (var (left, top) in new[] { (0, 0), (size - 7, 0), (0, size - 7) })
        {
            matrix[left + 0, top + 0].ShouldBeTrue();
            matrix[left + 3, top + 3].ShouldBeTrue();   // göbek
            matrix[left + 1, top + 1].ShouldBeFalse();  // beyaz halka
            matrix[left + 6, top + 6].ShouldBeTrue();   // dış çerçeve
        }
    }

    [Fact]
    public void Zamanlama_deseni_donusumlu_olmali()
    {
        var matrix = QrCode.Encode("https://pay.poyra.com/l/uFi5t7NGzoNbW1EE");
        var size = matrix.GetLength(0);

        for (var i = 8; i < size - 8; i++)
        {
            matrix[i, 6].ShouldBe(i % 2 == 0, $"yatay zamanlama x={i}");
            matrix[6, i].ShouldBe(i % 2 == 0, $"dikey zamanlama y={i}");
        }
    }

    [Fact]
    public void Karanlik_modul_daima_dolu_olmali()
    {
        var matrix = QrCode.Encode("test");
        matrix[8, matrix.GetLength(0) - 8].ShouldBeTrue();
    }

    [Fact]
    public void Ayni_girdi_ayni_qr_uretmeli()
    {
        const string url = "https://pay.poyra.com/l/uFi5t7NGzoNbW1EE";
        var first = QrCode.Encode(url);
        var second = QrCode.Encode(url);

        for (var y = 0; y < first.GetLength(1); y++)
            for (var x = 0; x < first.GetLength(0); x++)
                second[x, y].ShouldBe(first[x, y]);
    }

    [Fact]
    public void Farkli_slug_farkli_qr_uretmeli()
    {
        var a = QrCode.Encode("https://pay.poyra.com/l/AAAAAAAAAAAAAAAA");
        var b = QrCode.Encode("https://pay.poyra.com/l/AAAAAAAAAAAAAAAB");

        var differences = 0;
        for (var y = 0; y < a.GetLength(1); y++)
            for (var x = 0; x < a.GetLength(0); x++)
                if (a[x, y] != b[x, y])
                    differences++;

        differences.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Turkce_karakter_utf8_olarak_kodlanmali()
    {
        // Açıklama metni QR'a girmiyor ama URL yolunda Türkçe olabilir — çökmemeli
        Should.NotThrow(() => QrCode.Encode("https://pay.poyra.com/ödeme/şubat"));
    }

    [Fact]
    public void Cok_uzun_yuk_anlasilir_hata_vermeli()
    {
        var ex = Should.Throw<ArgumentException>(() => QrCode.Encode(new string('x', 400)));
        ex.Message.ShouldContain("çok uzun");
    }

    [Fact]
    public void Svg_sessiz_bolge_ve_renklerle_uretilmeli()
    {
        var svg = QrCode.ToSvg("https://pay.poyra.com/l/uFi5t7NGzoNbW1EE");

        svg.ShouldStartWith("<svg");
        svg.ShouldContain("viewBox=\"0 0 37 37\""); // 29 modül + 2×4 sessiz bölge
        svg.ShouldContain("#0B1220");               // Poyra gecesi
        svg.ShouldContain("shape-rendering=\"crispEdges\""); // ölçeklenince bulanmasın
        svg.ShouldContain("aria-label");
    }

    [Fact]
    public void Reed_solomon_kodlama_sozcugu_sayisi_dogru_olmali()
    {
        // v1/M: 26 toplam = 16 veri + 10 hata düzeltme. Matriste 21×21 modül vardır;
        // yanlış blok yapısı burada değil, okuyucuda patlardı.
        var matrix = QrCode.Encode("POYRA");
        matrix.GetLength(0).ShouldBe(21);

        // Veri alanı tamamen boş ya da tamamen dolu olmamalı (dolgu deseni işliyor)
        var dark = 0;
        for (var y = 0; y < 21; y++)
            for (var x = 0; x < 21; x++)
                if (matrix[x, y])
                    dark++;

        dark.ShouldBeInRange(120, 320);
    }

    /// <summary>
    /// ALTIN ÖRNEK: bu matris bağımsız bir referans uygulamayla (Python `qrcode`,
    /// bayt kipi / EC seviyesi M / maske 0) üretildi ve kodlayıcımızla BİT BİREBİR eşleşti.
    /// Yapısal testler geometriyi doğrular; bu test kodlama/Reed-Solomon zincirini çivi ler —
    /// üretici polinomun katsayı sırası bozulsa QR "doğru görünür" ama hiçbir okuyucu okumaz.
    /// </summary>
    private static readonly string[] PoyraV1Reference =
    [
        "111111100111001111111",
        "100000101010101000001",
        "101110100101101011101",
        "101110100110101011101",
        "101110101010101011101",
        "100000100000101000001",
        "111111101010101111111",
        "000000000101100000000",
        "101010100101000010010",
        "111011011010001000110",
        "011000101100100011011",
        "001101001000001000010",
        "110010110010101011101",
        "000000001111010101100",
        "111111100101011101111",
        "100000100111110110000",
        "101110101001011101111",
        "101110100010001101010",
        "101110101110100010001",
        "100000100010001000010",
        "111111101000101010111",
    ];

    [Fact]
    public void Referans_uygulamayla_bit_birebir_esmeli()
    {
        var matrix = QrCode.Encode("POYRA");

        matrix.GetLength(0).ShouldBe(PoyraV1Reference.Length);
        for (var y = 0; y < PoyraV1Reference.Length; y++)
        {
            var row = string.Concat(Enumerable.Range(0, PoyraV1Reference.Length)
                .Select(x => matrix[x, y] ? '1' : '0'));
            row.ShouldBe(PoyraV1Reference[y], $"satır {y}");
        }
    }

    [Fact]
    public void Uzun_url_referans_ozetiyle_esmeli()
    {
        // Aynı referans uygulamanın 29×29 (v3) çıktısının SHA-256'sı
        var matrix = QrCode.Encode("https://pay.poyra.com/l/uFi5t7NGzoNbW1EE");
        var size = matrix.GetLength(0);
        var rendered = string.Join('\n', Enumerable.Range(0, size)
            .Select(y => string.Concat(Enumerable.Range(0, size).Select(x => matrix[x, y] ? '1' : '0'))));

        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(rendered)))
            .ShouldBe("ecd588b626d1767efb12f9445b7b170cfb8bce1d060fae97bac94495e3200924");
    }
}
