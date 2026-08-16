using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poyra.SharedKernel.Rules;


public sealed class RuleCondition
{
    [JsonPropertyName("all")] public List<RuleCondition>? All { get; set; }
    [JsonPropertyName("any")] public List<RuleCondition>? Any { get; set; }
    [JsonPropertyName("fact")] public string? Fact { get; set; }
    [JsonPropertyName("op")] public string? Op { get; set; }
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
}

public sealed class RuleFacts
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public RuleFacts Set(string fact, long? value)
    {
        if (value is not null) _values[fact] = value.Value;
        return this;
    }

    public RuleFacts Set(string fact, string? value)
    {
        if (!string.IsNullOrEmpty(value)) _values[fact] = value;
        return this;
    }

    public RuleFacts Set(string fact, bool? value)
    {
        if (value is not null) _values[fact] = value.Value;
        return this;
    }

    public RuleFacts Alias(string alias, string canonical)
    {
        if (_values.TryGetValue(canonical, out var value)) _values[alias] = value;
        return this;
    }

    public object? Get(string fact) => _values.GetValueOrDefault(fact);

    public IReadOnlyDictionary<string, object?> Snapshot() => _values;
}

public static class RuleEngine
{
    public static bool Evaluate(RuleCondition condition, RuleFacts facts)
    {
        if (condition.All is { Count: > 0 })
            return condition.All.All(c => Evaluate(c, facts));
        if (condition.Any is { Count: > 0 })
            return condition.Any.Any(c => Evaluate(c, facts));
        if (condition.Fact is null || condition.Op is null)
            return false;

        return Compare(facts.Get(condition.Fact), condition.Op, condition.Value);
    }

    private static bool Compare(object? actual, string op, JsonElement value) => actual switch
    {
        long number => Numeric(number, op, value),
        string text => Text(text, op, value),
        bool flag => Boolean(flag, op, value),
        _ => false, // bilinmeyen sinyal = eşleşmez
    };

    private static bool Numeric(long actual, string op, JsonElement value)
    {
        if (op is "in")
            return value.ValueKind == JsonValueKind.Array
                   && value.EnumerateArray().Any(v => v.TryGetInt64(out var x) && x == actual);

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var expected))
            return false;

        return op switch
        {
            "eq" => actual == expected,
            "ne" => actual != expected,
            "gt" => actual > expected,
            "gte" => actual >= expected,
            "lt" => actual < expected,
            "lte" => actual <= expected,
            _ => false,
        };
    }

    private static bool Text(string actual, string op, JsonElement value)
    {
        if (op is "in")
            return value.ValueKind == JsonValueKind.Array
                   && value.EnumerateArray().Any(v =>
                       string.Equals(v.GetString(), actual, StringComparison.OrdinalIgnoreCase));

        if (op is "starts_with")
            return value.ValueKind == JsonValueKind.String
                   && actual.StartsWith(value.GetString() ?? "", StringComparison.OrdinalIgnoreCase);

        if (op is "contains")
            return value.ValueKind == JsonValueKind.String
                   && actual.Contains(value.GetString() ?? "", StringComparison.OrdinalIgnoreCase);

        var expected = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return op switch
        {
            "eq" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "ne" => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool Boolean(bool actual, string op, JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;

        var expected = value.GetBoolean();
        return op switch
        {
            "eq" => actual == expected,
            "ne" => actual != expected,
            _ => false,
        };
    }
}
