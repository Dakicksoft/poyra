using Microsoft.AspNetCore.DataProtection;

namespace Poyra.Panel.Security;

public static class PanelFlash
{
    private const string Purpose = "Poyra.Panel.Flash";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static IDataProtector? _protector;

    public static void Initialize(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector(Purpose);

    public static string Protect(string message)
        => _protector is null
            ? message
            : _protector.Protect(
                $"{DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds()}|{message}");

    public static string? Reveal(string? raw)
    {
        if (raw is not { Length: > 0 } || _protector is null)
            return null;

        try
        {
            var payload = _protector.Unprotect(raw);
            var separator = payload.IndexOf('|');
            if (separator <= 0)
                return null;

            return long.TryParse(payload[..separator], out var expires)
                   && expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                ? payload[(separator + 1)..]
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
