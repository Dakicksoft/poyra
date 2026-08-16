using System.Security.Cryptography;
using System.Text;

namespace Poyra.Modules.Webhooks.Infrastructure;

public static class WebhookSigner
{
    public const string Header = "Poyra-Signature";

    public static string BuildHeader(string secret, long unixTime, string payload)
        => $"t={unixTime},v1={Sign(secret, unixTime, payload)}";

    public static string Sign(string secret, long unixTime, string payload)
        => Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{unixTime}.{payload}")));

    public static bool Verify(string secret, string signatureHeader, string payload, long? toleranceSeconds = null, long? nowUnix = null)
    {
        var parts = signatureHeader.Split(',', StringSplitOptions.TrimEntries);
        var t = parts.FirstOrDefault(p => p.StartsWith("t="))?[2..];
        var v1 = parts.FirstOrDefault(p => p.StartsWith("v1="))?[3..];
        if (t is null || v1 is null || !long.TryParse(t, out var unixTime))
            return false;

        if (toleranceSeconds is { } tolerance && nowUnix is { } now && Math.Abs(now - unixTime) > tolerance)
            return false;

        var expected = Sign(secret, unixTime, payload);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(v1), Encoding.UTF8.GetBytes(expected));
    }
}
