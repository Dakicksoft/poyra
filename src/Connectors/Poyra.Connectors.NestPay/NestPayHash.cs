using System.Security.Cryptography;
using System.Text;

namespace Poyra.Connectors.NestPay;

/// <summary>
/// NestPay (EST/Payten) "ver3" hash şeması:
/// parametre ADLARINA göre (küçük harfe indirgenmiş, ordinal) sıralanır; DEĞERLER
/// '\' → '\\' ve '|' → '\|' kaçışlanıp '|' ile birleştirilir; sona kaçışlanmış
/// store key eklenir; SHA-512 → Base64. Dönüşte 'hash' ve 'encoding' alanları hariç tutulur.
/// </summary>
public static class NestPayHash
{
    public static string ComputeVer3(IReadOnlyDictionary<string, string> fields, string storeKey)
    {
        var ordered = fields
            .Where(kv => !IsExcluded(kv.Key))
            .OrderBy(kv => kv.Key.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(kv => Escape(kv.Value ?? string.Empty));

        var plain = string.Join("|", ordered) + "|" + Escape(storeKey);
        return Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(plain)));
    }

    public static bool ValidateCallback(IReadOnlyDictionary<string, string> form, string storeKey)
    {
        var posted = form.FirstOrDefault(kv => kv.Key.Equals("HASH", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(posted))
            return false;

        var expected = ComputeVer3(form, storeKey);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(posted), Encoding.UTF8.GetBytes(expected));
    }

    private static bool IsExcluded(string key)
        => key.Equals("hash", StringComparison.OrdinalIgnoreCase)
           || key.Equals("encoding", StringComparison.OrdinalIgnoreCase)
           || key.Equals("countdown", StringComparison.OrdinalIgnoreCase);

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("|", "\\|");
}
