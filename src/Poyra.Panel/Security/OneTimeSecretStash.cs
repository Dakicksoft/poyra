using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Poyra.Panel.Security;

public sealed class OneTimeSecretStash(IMemoryCache cache)
{
    public const string CookieName = "poyra_cookie";
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    public string Put(string secret)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        cache.Set(CacheKey(key), secret, Ttl);
        return key;
    }

    public string? Take(string? key)
    {
        if (key is not { Length: > 0 })
            return null;

        var cacheKey = CacheKey(key);
        if (!cache.TryGetValue(cacheKey, out string? secret))
            return null;

        cache.Remove(cacheKey);
        return secret;
    }

    private static string CacheKey(string key) => $"one-time-secret:{key}";
}
