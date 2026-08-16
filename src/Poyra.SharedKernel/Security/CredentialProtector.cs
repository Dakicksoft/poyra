using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Poyra.SharedKernel.Security;

public interface ICredentialProtector
{
    byte[] Protect(IReadOnlyDictionary<string, string> values);
    IReadOnlyDictionary<string, string> Unprotect(byte[] blob);
}

public sealed class AesGcmCredentialProtector(IConfiguration configuration) : ICredentialProtector
{
    private const byte Version = 1;
    private readonly Lazy<byte[]> _key = new(() =>
    {
        var raw = configuration["Poyra:CredentialKey"]
            ?? throw new InvalidOperationException(
                "Poyra:CredentialKey tanımlı değil (base64, 32 bayt). Sır şifrelemesi bunsuz çalışmaz.");
        var key = Convert.FromBase64String(raw);
        return key.Length == 32
            ? key
            : throw new InvalidOperationException("Poyra:CredentialKey 32 bayt (base64) olmalı.");
    });

    public byte[] Protect(IReadOnlyDictionary<string, string> values)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(values);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];

        using var aes = new AesGcm(_key.Value, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);

        var blob = new byte[1 + 12 + 16 + cipher.Length];
        blob[0] = Version;
        nonce.CopyTo(blob, 1);
        tag.CopyTo(blob, 13);
        cipher.CopyTo(blob, 29);
        return blob;
    }

    public IReadOnlyDictionary<string, string> Unprotect(byte[] blob)
    {
        if (blob.Length < 29 || blob[0] != Version)
            throw new InvalidOperationException("Bilinmeyen sır zarf sürümü.");

        var nonce = blob.AsSpan(1, 12);
        var tag = blob.AsSpan(13, 16);
        var cipher = blob.AsSpan(29);
        var plaintext = new byte[cipher.Length];

        using var aes = new AesGcm(_key.Value, tagSizeInBytes: 16);
        aes.Decrypt(nonce, cipher, tag, plaintext);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)!;
    }
}
