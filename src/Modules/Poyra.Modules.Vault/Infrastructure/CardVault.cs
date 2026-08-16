using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Vault.Contracts;
using Poyra.SharedKernel.Security;

namespace Poyra.Modules.Vault.Infrastructure;

public sealed class VaultCrypto(IConfiguration configuration)
{
    private readonly Lazy<byte[]> _key = new(() =>
    {
        var raw = configuration["Poyra:VaultKey"]
            ?? throw new InvalidOperationException(
                "Poyra:VaultKey tanımlı değil (base64, 32 bayt). Kasa bunsuz çalışmaz.");
        var key = Convert.FromBase64String(raw);
        return key.Length == 32
            ? key
            : throw new InvalidOperationException("Poyra:VaultKey 32 bayt (base64) olmalı.");
    });

    private readonly Lazy<ICredentialProtector> _protector = new(() =>
        new AesGcmCredentialProtector(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Poyra:CredentialKey"] = configuration["Poyra:VaultKey"],
            })
            .Build()));

    public string Fingerprint(string pan)
        => Convert.ToHexStringLower(HMACSHA256.HashData(_key.Value, Encoding.UTF8.GetBytes(pan)));

    public byte[] Protect(CardData card)
        => _protector.Value.Protect(new Dictionary<string, string>
        {
            ["pan"] = card.Pan,
            ["exp_month"] = card.ExpiryMonth.ToString(),
            ["exp_year"] = card.ExpiryYear.ToString(),
            ["holder"] = card.HolderName ?? "",
        });

    public CardData Unprotect(byte[] blob)
    {
        var values = _protector.Value.Unprotect(blob);
        return new CardData(
            values["pan"],
            int.Parse(values["exp_month"]),
            int.Parse(values["exp_year"]),
            values.GetValueOrDefault("holder") is { Length: > 0 } holder ? holder : null,
            Cvv: null); // saklanmadı — tekrarlayan ödemede CVV'siz gidilir
    }
}

public sealed class CardVault(VaultDbContext db, VaultCrypto crypto) : ICardVault
{
    public async Task<VaultedCard?> ResolveAsync(string token, CancellationToken ct)
    {
        var record = await db.CardTokens.AsNoTracking()
            .SingleOrDefaultAsync(t => t.PublicToken == token && t.DeletedAt == null, ct);

        return record is null
            ? null
            : new VaultedCard(record.PublicToken, crypto.Unprotect(record.CardEncrypted),
                record.MaskedPan, record.Brand);
    }
}
