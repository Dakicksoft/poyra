using System.Text;

namespace Poyra.SharedKernel.Domain;

/// <summary>
/// Küçük bir QR kodlayıcı (ISO/IEC 18004): bayt kipi, hata düzeltme seviyesi M, sürüm 1-10.
/// Ödeme bağlantısı URL'leri için fazlasıyla yeterlidir (sürüm 10 / M ≈ 213 bayt).
///
/// Neden kütüphane değil: tek ihtiyacımız kısa bir URL'yi SVG'ye çevirmek. Bir bağımlılık
/// eklemek, ödeme yolundaki her güncellemede lisans ve tedarik zinciri denetimi demektir.
/// Kod kısa, deterministik ve testle çivilenmiştir — çıktı referans QR'larla doğrulanır.
/// </summary>
public static class QrCode
{
    /// <summary>Sürüm başına (1-10) M seviyesinde: toplam kodlama sözcüğü ve blok yapısı.</summary>
    private static readonly (int Total, int EcPerBlock, int Group1Blocks, int Group1Data,
        int Group2Blocks, int Group2Data)[] LevelM =
    [
        (26, 10, 1, 16, 0, 0),     // v1
        (44, 16, 1, 28, 0, 0),     // v2
        (70, 26, 1, 44, 0, 0),     // v3
        (100, 18, 2, 32, 0, 0),    // v4
        (134, 24, 2, 43, 0, 0),    // v5
        (172, 16, 4, 27, 0, 0),    // v6
        (196, 18, 4, 31, 0, 0),    // v7
        (242, 22, 2, 38, 2, 39),   // v8
        (292, 22, 3, 36, 2, 37),   // v9
        (346, 26, 4, 43, 1, 44),   // v10
    ];

    /// <summary>Sürüm başına hizalama deseni merkez koordinatları.</summary>
    private static readonly int[][] AlignmentCenters =
    [
        [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34],
        [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    /// <summary>Biçim bilgisi (EC seviyesi M = 00) — 8 maske için önceden hesaplanmış 15 bit.</summary>
    private static readonly int[] FormatBitsM =
        [0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0];

    public static bool[,] Encode(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var version = PickVersion(payload.Length)
            ?? throw new ArgumentException(
                $"QR yükü çok uzun ({payload.Length} bayt) — sürüm 10 / M sınırı aşıldı.", nameof(text));

        var spec = LevelM[version - 1];
        var dataCapacity = spec.Group1Blocks * spec.Group1Data + spec.Group2Blocks * spec.Group2Data;

        var bits = new BitBuffer();
        bits.Append(0b0100, 4);                                  // bayt kipi
        bits.Append(payload.Length, version <= 9 ? 8 : 16);      // uzunluk alanı
        foreach (var b in payload)
            bits.Append(b, 8);

        // Sonlandırıcı + bayt hizalama + dolgu deseni (spesifikasyon: 0xEC, 0x11 dönüşümlü)
        var capacityBits = dataCapacity * 8;
        bits.Append(0, Math.Min(4, capacityBits - bits.Length));
        while (bits.Length % 8 != 0)
            bits.Append(0, 1);

        var padToggle = true;
        while (bits.Length < capacityBits)
        {
            bits.Append(padToggle ? 0xEC : 0x11, 8);
            padToggle = !padToggle;
        }

        var interleaved = InterleaveWithEcc(bits.ToBytes(), spec);
        return BuildMatrix(version, interleaved);
    }

    /// <summary>QR'ı ölçeklenebilir SVG'ye çevirir — baskıda ve ekranda kırılmaz.</summary>
    public static string ToSvg(string text, int quietZone = 4, string dark = "#0B1220", string light = "#FFFFFF")
    {
        var matrix = Encode(text);
        var size = matrix.GetLength(0);
        var total = size + quietZone * 2;

        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (matrix[x, y])
                    path.Append($"M{x + quietZone} {y + quietZone}h1v1h-1z");
            }
        }

        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {total} {total}" shape-rendering="crispEdges" role="img" aria-label="QR kod">
            <rect width="{total}" height="{total}" fill="{light}"/>
            <path d="{path}" fill="{dark}"/>
            </svg>
            """;
    }

    private static int? PickVersion(int byteCount)
    {
        for (var version = 1; version <= LevelM.Length; version++)
        {
            var spec = LevelM[version - 1];
            var dataBytes = spec.Group1Blocks * spec.Group1Data + spec.Group2Blocks * spec.Group2Data;
            var headerBits = 4 + (version <= 9 ? 8 : 16);
            if (dataBytes * 8 >= headerBits + byteCount * 8)
                return version;
        }

        return null;
    }

    private static byte[] InterleaveWithEcc(
        byte[] data,
        (int Total, int EcPerBlock, int Group1Blocks, int Group1Data, int Group2Blocks, int Group2Data) spec)
    {
        var blocks = new List<byte[]>();
        var ecBlocks = new List<byte[]>();
        var offset = 0;

        void AddBlocks(int count, int size)
        {
            for (var i = 0; i < count; i++)
            {
                var block = data[offset..(offset + size)];
                offset += size;
                blocks.Add(block);
                ecBlocks.Add(ReedSolomon.Encode(block, spec.EcPerBlock));
            }
        }

        AddBlocks(spec.Group1Blocks, spec.Group1Data);
        AddBlocks(spec.Group2Blocks, spec.Group2Data);

        var result = new List<byte>(spec.Total);
        var maxData = blocks.Max(b => b.Length);
        for (var i = 0; i < maxData; i++)
            foreach (var block in blocks.Where(block => i < block.Length))
                result.Add(block[i]);

        for (var i = 0; i < spec.EcPerBlock; i++)
            result.AddRange(ecBlocks.Select(block => block[i]));

        return [.. result];
    }

    private static bool[,] BuildMatrix(int version, byte[] payload)
    {
        var size = version * 4 + 17;
        var modules = new bool[size, size];
        var reserved = new bool[size, size];

        PlaceFinder(modules, reserved, 0, 0, size);
        PlaceFinder(modules, reserved, size - 7, 0, size);
        PlaceFinder(modules, reserved, 0, size - 7, size);
        PlaceTiming(modules, reserved, size);
        PlaceAlignment(modules, reserved, version, size);
        ReserveFormat(reserved, size);

        // Karanlık modül (spesifikasyon gereği daima 1)
        modules[8, size - 8] = true;
        reserved[8, size - 8] = true;

        PlaceData(modules, reserved, payload, size);

        // Maske 0 sabit: kalite optimizasyonu (8 maskeden en iyisini seçmek) okunurluğu
        // marjinal iyileştirir; URL yükleri için maske 0 her okuyucuda çalışır ve kod yarı yarıya kısalır.
        ApplyMask(modules, reserved, size);
        PlaceFormat(modules, size);

        return modules;
    }

    private static void PlaceFinder(bool[,] modules, bool[,] reserved, int left, int top, int size)
    {
        for (var y = -1; y <= 7; y++)
        {
            for (var x = -1; x <= 7; x++)
            {
                var px = left + x;
                var py = top + y;
                if (px < 0 || py < 0 || px >= size || py >= size)
                    continue;

                var inRing = x is >= 0 and <= 6 && y is >= 0 and <= 6
                             && (x is 0 or 6 || y is 0 or 6 || (x is >= 2 and <= 4 && y is >= 2 and <= 4));
                modules[px, py] = inRing;
                reserved[px, py] = true;
            }
        }
    }

    private static void PlaceTiming(bool[,] modules, bool[,] reserved, int size)
    {
        for (var i = 8; i < size - 8; i++)
        {
            var on = i % 2 == 0;
            modules[i, 6] = on;
            modules[6, i] = on;
            reserved[i, 6] = true;
            reserved[6, i] = true;
        }
    }

    private static void PlaceAlignment(bool[,] modules, bool[,] reserved, int version, int size)
    {
        var centers = AlignmentCenters[version - 1];
        foreach (var cy in centers)
        {
            foreach (var cx in centers)
            {
                // Bulucu desenlerinin üstüne yazılmaz
                if ((cx <= 8 && cy <= 8) || (cx <= 8 && cy >= size - 9) || (cx >= size - 9 && cy <= 8))
                    continue;

                for (var y = -2; y <= 2; y++)
                {
                    for (var x = -2; x <= 2; x++)
                    {
                        modules[cx + x, cy + y] = Math.Max(Math.Abs(x), Math.Abs(y)) != 1;
                        reserved[cx + x, cy + y] = true;
                    }
                }
            }
        }
    }

    private static void ReserveFormat(bool[,] reserved, int size)
    {
        for (var i = 0; i <= 8; i++)
        {
            reserved[i, 8] = true;
            reserved[8, i] = true;
        }

        for (var i = 0; i < 8; i++)
        {
            reserved[size - 1 - i, 8] = true;
            reserved[8, size - 1 - i] = true;
        }
    }

    private static void PlaceData(bool[,] modules, bool[,] reserved, byte[] payload, int size)
    {
        var bitIndex = 0;
        var upward = true;

        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
                right = 5; // dikey zamanlama sütunu atlanır

            for (var step = 0; step < size; step++)
            {
                var y = upward ? size - 1 - step : step;
                for (var column = 0; column < 2; column++)
                {
                    var x = right - column;
                    if (reserved[x, y])
                        continue;

                    var bit = bitIndex < payload.Length * 8
                              && (payload[bitIndex / 8] >> (7 - bitIndex % 8) & 1) == 1;
                    modules[x, y] = bit;
                    bitIndex++;
                }
            }

            upward = !upward;
        }
    }

    /// <summary>Maske 0: (satır + sütun) % 2 == 0 olan veri modülleri ters çevrilir.</summary>
    private static void ApplyMask(bool[,] modules, bool[,] reserved, int size)
    {
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (!reserved[x, y] && (y + x) % 2 == 0)
                    modules[x, y] = !modules[x, y];
    }

    private static void PlaceFormat(bool[,] modules, int size)
    {
        var format = FormatBitsM[0]; // maske 0

        for (var i = 0; i < 15; i++)
        {
            var bit = (format >> i & 1) == 1;

            // Sol üst
            if (i < 6)
                modules[8, i] = bit;
            else if (i == 6)
                modules[8, 7] = bit;
            else if (i == 7)
                modules[8, 8] = bit;
            else if (i == 8)
                modules[7, 8] = bit;
            else
                modules[14 - i, 8] = bit;

            // İkinci kopya (sağ üst + sol alt)
            if (i < 8)
                modules[size - 1 - i, 8] = bit;
            else
                modules[8, size - 15 + i] = bit;
        }
    }

    private sealed class BitBuffer
    {
        private readonly List<byte> _bytes = [];
        private int _bitsInLast;

        public int Length => _bytes.Count * 8 - (_bitsInLast == 0 ? 0 : 8 - _bitsInLast);

        public void Append(int value, int bitCount)
        {
            for (var i = bitCount - 1; i >= 0; i--)
            {
                if (_bitsInLast == 0)
                {
                    _bytes.Add(0);
                    _bitsInLast = 0;
                }

                var bit = (value >> i & 1) == 1;
                if (bit)
                    _bytes[^1] |= (byte)(1 << (7 - _bitsInLast));

                _bitsInLast = (_bitsInLast + 1) % 8;
            }
        }

        public byte[] ToBytes() => [.. _bytes];
    }
}

/// <summary>GF(256) üzerinde Reed-Solomon hata düzeltme (QR'ın kullandığı 0x11D polinomu).</summary>
internal static class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static ReedSolomon()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256)
                x ^= 0x11D;
        }

        for (var i = 255; i < 512; i++)
            Exp[i] = Exp[i - 255];
    }

    /// <summary>
    /// Veri polinomunu üretici polinoma böler; kalan hata düzeltme kodlama sözcükleridir.
    /// Katsayılar AZALAN derece sırasındadır (generator[0] = x^degree katsayısı = 1);
    /// sıra karışırsa üretilen QR yapısal olarak doğru görünür ama HİÇBİR OKUYUCU okuyamaz.
    /// </summary>
    public static byte[] Encode(byte[] data, int eccLength)
    {
        var generator = Generator(eccLength);
        var remainder = new byte[data.Length + eccLength];
        Array.Copy(data, remainder, data.Length);

        for (var i = 0; i < data.Length; i++)
        {
            var coefficient = remainder[i];
            if (coefficient == 0)
                continue;

            for (var j = 1; j <= eccLength; j++)
                remainder[i + j] ^= Multiply(generator[j], coefficient);
        }

        return remainder[data.Length..];
    }

    /// <summary>g(x) = ∏(x − α^i), i = 0..degree-1 — azalan derece sırasında.</summary>
    private static byte[] Generator(int degree)
    {
        var result = new byte[] { 1 };

        for (var i = 0; i < degree; i++)
        {
            var next = new byte[result.Length + 1];
            for (var j = 0; j < result.Length; j++)
            {
                next[j] ^= result[j];                          // × x
                next[j + 1] ^= Multiply(result[j], Exp[i]);    // × α^i
            }

            result = next;
        }

        return result;
    }

    private static byte Multiply(byte a, byte b)
        => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
}
