using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Tenancy.Infrastructure;

public sealed record JwtPrincipal(Guid UserId, string Email, Guid TenantId, TenantRole Role);

/// <summary>
/// Kısa ömürlü (10 dk) HS256 erişim belirteci. Uzun oturum, döndürülen refresh token'la sürer.
/// Anahtar: Poyra:JwtKey (base64, 32 bayt) — CredentialKey'den AYRI tutulur ki
/// biri sızdığında öteki dönmesin.
/// </summary>
public sealed class JwtTokens(IConfiguration configuration, IClock clock)
{
    public const string Issuer = "poyra";
    public const string Audience = "poyra-panel";
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(10);

    private readonly JsonWebTokenHandler _handler = new();
    private readonly Lazy<SymmetricSecurityKey> _key = new(() =>
    {
        var raw = configuration["Poyra:JwtKey"]
            ?? throw new InvalidOperationException("Poyra:JwtKey tanımlı değil (base64, 32 bayt).");
        var bytes = Convert.FromBase64String(raw);
        return bytes.Length == 32
            ? new SymmetricSecurityKey(bytes)
            : throw new InvalidOperationException("Poyra:JwtKey 32 bayt (base64) olmalı.");
    });

    public string Issue(JwtPrincipal principal)
        => _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = clock.UtcNow.UtcDateTime,
            NotBefore = clock.UtcNow.UtcDateTime,
            Expires = (clock.UtcNow + AccessTokenLifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(_key.Value, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = principal.UserId.ToString(),
                ["email"] = principal.Email,
                ["tid"] = principal.TenantId.ToString(),
                ["role"] = TenantRoleMap.ToDb[principal.Role],
            },
        });

    public async Task<JwtPrincipal?> ValidateAsync(string token)
    {
        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = _key.Value,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        });

        if (!result.IsValid)
            return null;

        var claims = result.Claims;
        if (!claims.TryGetValue("sub", out var sub) || !Guid.TryParse(sub.ToString(), out var userId)
            || !claims.TryGetValue("tid", out var tid) || !Guid.TryParse(tid.ToString(), out var tenantId)
            || !claims.TryGetValue("role", out var role)
            || !TenantRoleMap.FromDb.TryGetValue(role.ToString() ?? "", out var parsedRole))
            return null;

        claims.TryGetValue("email", out var email);
        return new JwtPrincipal(userId, email?.ToString() ?? "", tenantId, parsedRole);
    }
}
