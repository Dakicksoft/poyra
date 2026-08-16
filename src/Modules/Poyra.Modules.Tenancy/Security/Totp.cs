using System.Security.Cryptography;
using System.Text;

namespace Poyra.Modules.Tenancy.Security;

/// <summary>
/// TOTP (RFC 6238, HMAC-SHA1, 30 sn adım, 6 hane) — Google Authenticator/Authy/1Password
/// uyumlu.
///
/// Doğrulamada ±1 adım tolerans tanınır (saat kayması); daha genişi güvenliği zayıflatır.
/// Sır 160 bit rastgeleliktir (RFC 4226 önerisi) ve veritabanında AES-GCM korumalı durur.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    private const int ToleranceSteps = 1;

    /// <summary>160 bit yeni sır — Base32 (authenticator uygulamalarının beklediği biçim).</summary>
    public static string GenerateSecret()
        => Base32Encode(RandomNumberGenerator.GetBytes(20));

    /// <summary>Kod doğrulama; sabit zamanlı karşılaştırma ile.</summary>
    public static bool Verify(string base32Secret, string code, DateTimeOffset now)
    {
        if (code is not { Length: Digits } || !code.All(char.IsAsciiDigit))
            return false;

        var key = Base32Decode(base32Secret);
        var step = now.ToUnixTimeSeconds() / StepSeconds;

        for (var offset = -ToleranceSteps; offset <= ToleranceSteps; offset++)
        {
            var expected = Compute(key, step + offset);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(code)))
                return true;
        }

        return false;
    }

    /// <summary>Authenticator uygulamasının okuduğu kurulum adresi (QR içeriği).</summary>
    public static string BuildOtpauthUri(string accountEmail, string base32Secret)
        => $"otpauth://totp/{Uri.EscapeDataString("Poyra")}:{Uri.EscapeDataString(accountEmail)}"
           + $"?secret={base32Secret}&issuer={Uri.EscapeDataString("Poyra")}"
           + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    private static string Compute(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6");
    }

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Base32Alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string encoded)
    {
        var result = new List<byte>(encoded.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in encoded.TrimEnd('='))
        {
            var index = Base32Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (index < 0)
                continue; // boşluk/tire gibi ayraçlar elle girişte tolere edilir

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                result.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return [.. result];
    }
}
