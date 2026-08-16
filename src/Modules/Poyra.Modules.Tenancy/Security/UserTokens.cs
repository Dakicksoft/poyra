using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Poyra.Modules.Tenancy.Domain;

namespace Poyra.Modules.Tenancy.Security;

public static class UserTokens
{
    public const string ResetPrefix = "prst_";
    public const string VerifyPrefix = "evrf_";

    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan VerifyLifetime = TimeSpan.FromDays(3);

    public static string Generate(UserTokenPurpose purpose)
        => Prefix(purpose) + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(token)));

    public static TimeSpan Lifetime(UserTokenPurpose purpose)
        => purpose == UserTokenPurpose.PasswordReset ? ResetLifetime : VerifyLifetime;

    private static string Prefix(UserTokenPurpose purpose)
        => purpose == UserTokenPurpose.PasswordReset ? ResetPrefix : VerifyPrefix;
}
