using System.Security.Cryptography;
using System.Text;
using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Payments.Domain;


public sealed class IdempotencyRecord : ITenantOwned, IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }

    public required string Key { get; init; }

    public required string Endpoint { get; init; }

    public required string RequestHash { get; init; }

    public int? StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    public bool IsCompleted => CompletedAt is not null;

    public bool IsStale(DateTimeOffset now) => !IsCompleted && now - CreatedAt > StaleAfter;

    public static string Hash(string endpoint, string body)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{endpoint}\n{body}")));
}
