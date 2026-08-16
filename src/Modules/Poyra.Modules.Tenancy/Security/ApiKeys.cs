using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Poyra.Modules.Tenancy.Security;

public static class ApiKeys
{
    public const string TestPrefix = "sk_test_";
    public const string LivePrefix = "sk_live_";

    public static string Generate(bool live = false)
        => (live ? LivePrefix : TestPrefix) + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string key)
        => Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Panelde gösterim için baş kısım, ör: "sk_test_AbC1".</summary>
    public static string PrefixHint(string key) => key[..Math.Min(12, key.Length)];
}
