using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Poyra.Modules.Tenancy.Security;

public static class RefreshTokens
{
    public const string Prefix = "prt_";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public static string Generate()
        => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(token)));
}
