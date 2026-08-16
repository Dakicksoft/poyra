using System.Text.Json;
using System.Text.Json.Serialization;
using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Rules;

namespace Poyra.Modules.Risk.Domain;

public sealed class RiskRuleSet : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public int Version { get; init; }
    public required string Document { get; init; }
    public bool Active { get; set; }
    public Guid? PublishedByUserId { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum BlocklistKind
{
    /// <summary>Maskeli kart (ilk6+son4) — PCI dışıdır, tam PAN saklanmaz.</summary>
    Card = 10,
    Email = 20,
    Ip = 30,
    CustomerRef = 40,
    Bin = 50,
    Country = 60,
}

public static class BlocklistKindMap
{
    public static readonly IReadOnlyDictionary<BlocklistKind, string> ToDb =
        new Dictionary<BlocklistKind, string>
        {
            [BlocklistKind.Card] = "card",
            [BlocklistKind.Email] = "email",
            [BlocklistKind.Ip] = "ip",
            [BlocklistKind.CustomerRef] = "customer_ref",
            [BlocklistKind.Bin] = "bin",
            [BlocklistKind.Country] = "country",
        };

    public static readonly IReadOnlyDictionary<string, BlocklistKind> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

public sealed class BlocklistEntry : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public BlocklistKind Kind { get; init; }

    /// <summary>Normalleştirilmiş değer (küçük harf, kırpılmış).</summary>
    public required string Value { get; init; }

    public required string Reason { get; init; }
    public Guid? AddedByUserId { get; init; }

    /// <summary>Süreli engel — null ise süresiz.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? RemovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsActive(DateTimeOffset now)
        => RemovedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public static string Normalize(string value)
        => TurkishText.Fold(value);
}

public sealed class RiskAssessment : ITenantOwned, IHasCreatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public required string PaymentId { get; init; }
    public required string Outcome { get; init; }
    public string? RuleName { get; init; }
    public string? Reason { get; init; }
    public int? RuleVersion { get; init; }
    public required string Flow { get; init; }

    /// <summary>Kararın dayandığı sinyaller (jsonb).</summary>
    public string Signals { get; init; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}


/// <summary>
/// Risk kural dokümanı. Koşul dili rota motoruyla AYNIDIR (çekirdekteki RuleEngine);
/// farkı sonuçtur: rota POS seçer, risk karar verir.
///
/// {
///   "rules": [
///     { "name": "gece yüksek tutar", "outcome": "challenge",
///       "when": { "all": [ { "fact": "amount_minor", "op": "gte", "value": 500000 },
///                          { "fact": "hour", "op": "gte", "value": 1 } ] },
///       "reason": "Gece yarısı yüksek tutar — 3DS zorunlu" }
///   ],
///   "default": "allow"
/// }
/// </summary>
public sealed class RiskDocument
{
    [JsonPropertyName("rules")] public List<RiskRule> Rules { get; set; } = [];

    /// <summary>Hiçbir kural eşleşmediğinde. Varsayılan bilinçli olarak 'allow'.</summary>
    [JsonPropertyName("default")] public string Default { get; set; } = "allow";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static RiskDocument Parse(string json)
        => JsonSerializer.Deserialize<RiskDocument>(json, Options)
           ?? throw new JsonException("Boş risk dokümanı.");

    public static string Serialize(RiskDocument document) => JsonSerializer.Serialize(document, Options);
}

public sealed class RiskRule
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("when")] public RuleCondition? When { get; set; }
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = "review";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}
