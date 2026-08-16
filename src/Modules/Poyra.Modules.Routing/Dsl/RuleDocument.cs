using System.Text.Json;
using System.Text.Json.Serialization;
using Poyra.SharedKernel.Rules;

namespace Poyra.Modules.Routing.Dsl;

/// <summary>
/// Kural DSL'i. Örnek:
/// {
///   "rules": [ { "name": "yüksek tutar",
///                "when": { "all": [ { "fact": "amount_minor", "op": "gte", "value": 100000 } ] },
///                "route": ["İş POS"], "reason": "yüksek tutar → İş POS" } ],
///   "volumeSplit": [ { "account": "İş POS", "percent": 80 }, { "account": "Mock", "percent": 20 } ],
///   "fallback": ["Mock"],
///   "guards": { "skipUnhealthy": true, "maxAttempts": 2 }
/// }
/// Hesaplar etiketle (label) veya Guid ile referanslanır.
/// </summary>
public sealed class RuleDocument
{
    [JsonPropertyName("rules")] public List<RouteRule> Rules { get; set; } = [];
    [JsonPropertyName("volumeSplit")] public List<VolumeSplitEntry> VolumeSplit { get; set; } = [];
    [JsonPropertyName("fallback")] public List<string> Fallback { get; set; } = [];
    [JsonPropertyName("guards")] public RuleGuards Guards { get; set; } = new();

    /// <summary>
    /// Kural eşleşmediğinde uygulanacak varsayılan strateji:
    /// priority | cheapest | best_success | fastest | balanced (bkz. RoutingStrategies).
    /// </summary>
    [JsonPropertyName("strategy")] public string? Strategy { get; set; }

    /// <summary>balanced stratejisinin ağırlıkları (toplamı 1 olmak zorunda değil).</summary>
    [JsonPropertyName("weights")] public StrategyWeights Weights { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static RuleDocument Parse(string json)
        => JsonSerializer.Deserialize<RuleDocument>(json, Options)
           ?? throw new JsonException("Boş kural dokümanı.");
}

public sealed class RouteRule
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("when")] public RuleCondition? When { get; set; }

    /// <summary>Sabit hesap listesi. Boşsa kural yalnız strateji seçer (route+strategy birlikte de olur).</summary>
    [JsonPropertyName("route")] public List<string> Route { get; set; } = [];

    /// <summary>Bu kural eşleşince uygulanacak strateji — doküman varsayılanını ezer.</summary>
    [JsonPropertyName("strategy")] public string? Strategy { get; set; }

    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class StrategyWeights
{
    [JsonPropertyName("cost")] public double Cost { get; set; } = 1;
    [JsonPropertyName("success")] public double Success { get; set; } = 1;
    [JsonPropertyName("latency")] public double Latency { get; set; } = 0.5;
}

public sealed class VolumeSplitEntry
{
    [JsonPropertyName("account")] public string Account { get; set; } = "";
    [JsonPropertyName("percent")] public int Percent { get; set; }
}

public sealed class RuleGuards
{
    [JsonPropertyName("skipUnhealthy")] public bool SkipUnhealthy { get; set; } = true;
    [JsonPropertyName("maxAttempts")] public int MaxAttempts { get; set; } = 2;
}
