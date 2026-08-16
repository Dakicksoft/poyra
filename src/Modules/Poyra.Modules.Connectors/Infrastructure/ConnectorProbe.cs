using Poyra.Connectors.Abstractions;
using Poyra.Modules.Connectors.Domain;
using Poyra.SharedKernel.Security;

namespace Poyra.Modules.Connectors.Infrastructure;

/// <summary>
/// Tek yoklama noktası: canary işi, elle "test et" ucu ve kimlik yenileme aynı davranışı
/// paylaşır. Yoklama İSTİSNA FIRLATMAZ — bir POS'un erişilemez olması bir hata değil,
/// ölçülecek bir olgudur; çağıran taraf kararı verir. null = konnektör yoklama desteklemiyor.
/// </summary>
public static class ConnectorProbe
{
    public static async Task<ConnectorProbeResult?> RunAsync(
        ConnectorRegistry registry,
        ICredentialProtector protector,
        ConnectorAccount account,
        CancellationToken ct)
    {
        try
        {
            var connector = registry.Get(account.ConnectorKey);
            var credentials = new ConnectorCredentials(protector.Unprotect(account.CredentialsEncrypted));
            return await connector.ProbeAsync(credentials, ct);
        }
        catch (Exception ex)
        {
            // Şifre çözme hatası, bilinmeyen konnektör, ağ hatası — hepsi "erişilemiyor"dur
            return new ConnectorProbeResult(false, $"probe hatası: {ex.Message}");
        }
    }
}
