using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Poyra.Modules.Routing.Dsl;
using Poyra.SharedKernel.Rules;
using Poyra.SharedKernel.Domain;

namespace Poyra.Panel.Routing;

public enum FactKind
{
    Money,   // kullanıcı ₺ görür, DSL kuruş taşır
    Number,
    Text,
    Choice,
    Boolean,
}

/// <param name="Fact">DSL'deki fact adı — motorun tanıdığı kanonik ad.</param>
/// <param name="Aliases">Aynı fact'in DSL'de kabul edilen diğer yazımları (geri okuma için).</param>
public sealed record FactDefinition(
    string Fact,
    string Label,
    FactKind Kind,
    string? Hint = null,
    IReadOnlyList<(string Value, string Label)>? Choices = null,
    IReadOnlyList<string>? Aliases = null);

/// <summary>
/// Görsel kurucunun bildiği fact kataloğu. Motorun <see cref="RuleEvaluator"/> içindeki
/// switch'iyle BİREBİR aynı olmalıdır; buraya eklenmeyen bir fact arayüzde görünmez ama
/// JSON'a elle yazılabilir (kurucu bilmediği fact'i "gelişmiş" sayıp JSON'a düşer).
/// </summary>
public static class RuleFacts
{
    public static readonly IReadOnlyList<FactDefinition> All =
    [
        new("amount_minor", "Tutar", FactKind.Money, "₺ olarak yazın — kurala kuruş yazılır"),
        new("installments", "Taksit sayısı", FactKind.Number),
        new("hour", "Saat (TR, 0-23)", FactKind.Number, "Türkiye saati — gece toplu işler için"),
        new("currency", "Para birimi", FactKind.Choice, Choices:
            [("TRY", "TRY"), ("USD", "USD"), ("EUR", "EUR")]),
        new("channel", "Kanal", FactKind.Choice,
            "Ödemenin Poyra'ya girdiği yol — işyerinin kendi sitesi/uygulaması ayrımı değil",
            Choices:
            [("api", "API (işyeri sunucusu)"), ("link", "Ödeme linki"),
             ("field", "Saha tahsilatı"), ("subscription", "Abonelik yenilemesi")]),
        new("bin", "Kart BIN'i", FactKind.Text, "İlk 6-8 hane (PAN değil)"),
        new("card.bank", "Kart bankası (kod)", FactKind.Text,
            "0062 Garanti · 0064 İş · 0067 Yapı Kredi · 0046 Akbank — kendi POS bankanızla eşitse 'on-us'",
            Aliases: ["bank_code"]),
        new("card.program", "Kart programı", FactKind.Choice, Choices:
            [("bonus", "Bonus"), ("world", "World"), ("maximum", "Maximum"), ("axess", "Axess"),
             ("paraf", "Paraf"), ("cardfinans", "CardFinans"), ("advantage", "Advantage"),
             ("bankkart", "Bankkart")],
            Aliases: ["program"]),
        new("card.brand", "Kart markası", FactKind.Choice, Choices:
            [("visa", "Visa"), ("mastercard", "Mastercard"), ("troy", "Troy"), ("amex", "Amex")],
            Aliases: ["brand"]),
        new("card.type", "Kart tipi", FactKind.Choice, Choices:
            [("credit", "Kredi"), ("debit", "Banka"), ("prepaid", "Ön ödemeli")],
            Aliases: ["card_type"]),
        new("card.commercial", "Ticari kart", FactKind.Boolean, "Kurumsal/ticari kartlar"),
    ];

    public static FactDefinition? Find(string? fact)
    {
        if (fact is null)
            return null;

        var key = fact.Trim().ToLowerInvariant();
        return All.FirstOrDefault(f =>
            f.Fact == key || (f.Aliases?.Contains(key) ?? false));
    }

    public static IReadOnlyList<(string Op, string Label)> OpsFor(FactKind kind) => kind switch
    {
        FactKind.Money or FactKind.Number =>
        [
            ("eq", "eşitse"), ("ne", "eşit değilse"), ("gte", "en az"), ("gt", "büyükse"),
            ("lte", "en çok"), ("lt", "küçükse"), ("in", "şunlardan biriyse"),
        ],
        FactKind.Boolean => [("eq", "ise"), ("ne", "değilse")],
        _ =>
        [
            ("eq", "eşitse"), ("ne", "eşit değilse"),
            ("in", "şunlardan biriyse"), ("starts_with", "şununla başlıyorsa"),
        ],
    };
}

public sealed class ConditionRow
{
    public string Fact { get; set; } = "amount_minor";
    public string Op { get; set; } = "gte";

    /// <summary>Kullanıcının gördüğü metin. Money için ₺, "in" için virgülle ayrılmış liste.</summary>
    public string Value { get; set; } = "";

    public FactDefinition Definition => RuleFacts.Find(Fact) ?? RuleFacts.All[0];
}

public sealed class RuleBlock
{
    public string Name { get; set; } = "";
    public string Join { get; set; } = "all"; // all | any
    public List<ConditionRow> Conditions { get; set; } = [];
    public List<string> Route { get; set; } = [];
    public string Strategy { get; set; } = ""; // boş = doküman varsayılanı
    public string Reason { get; set; } = "";
}

public sealed class SplitRow
{
    public string Account { get; set; } = "";
    public int Percent { get; set; }
}
public sealed class RuleBuilderModel
{
    public string Strategy { get; set; } = "priority";
    public List<RuleBlock> Rules { get; set; } = [];
    public List<SplitRow> Split { get; set; } = [];
    public List<string> Fallback { get; set; } = [];
    public bool SkipUnhealthy { get; set; } = true;
    public int MaxAttempts { get; set; } = 2;
    public int ExplorePercent { get; set; } = 10;
    public double CostWeight { get; set; } = 1;
    public double SuccessWeight { get; set; } = 1;
    public double LatencyWeight { get; set; } = 0.5;

    public bool Advanced { get; private set; }

    /// <summary>
    /// Ölçüm kotası yalnız ÖLÇÜLEN sinyale dayanan stratejilerde iş görür; cheapest
    /// (anlaşma oranı) ve priority (hesap önceliği) trafikten beslenmez. Alan da yalnız
    /// o stratejiler seçiliyken gösterilir — dengeli ağırlıklar satırıyla aynı desen.
    /// </summary>
    public bool ShowExploreQuota
        => MeasuredStrategies.Contains(Strategy)
           || Rules.Any(r => r.Strategy is { } s && MeasuredStrategies.Contains(s));

    private static readonly string[] MeasuredStrategies = ["best_success", "fastest", "balanced"];

    public static readonly IReadOnlyList<(string Value, string Label)> Strategies =
    [
        ("priority", "Öncelik sırası"),
        ("cheapest", "En ucuz (komisyon)"),
        ("best_success", "En yüksek başarı"),
        ("fastest", "En hızlı yanıt"),
        ("balanced", "Dengeli (ağırlıklı)"),
    ];

    public static RuleBuilderModel FromJson(JsonElement document)
    {
        var model = new RuleBuilderModel();
        RuleDocument parsed;
        try
        {
            parsed = RuleDocument.Parse(document.GetRawText());
        }
        catch (JsonException)
        {
            model.Advanced = true;
            return model;
        }

        model.Strategy = parsed.Strategy ?? "priority";
        model.SkipUnhealthy = parsed.Guards.SkipUnhealthy;
        model.MaxAttempts = parsed.Guards.MaxAttempts;
        model.ExplorePercent = parsed.Guards.ExplorePercent;
        model.CostWeight = parsed.Weights.Cost;
        model.SuccessWeight = parsed.Weights.Success;
        model.LatencyWeight = parsed.Weights.Latency;
        model.Fallback = [.. parsed.Fallback];
        model.Split = parsed.VolumeSplit
            .Select(v => new SplitRow { Account = v.Account, Percent = v.Percent })
            .ToList();

        foreach (var rule in parsed.Rules)
        {
            var block = new RuleBlock
            {
                Name = rule.Name ?? "",
                Route = [.. rule.Route],
                Strategy = rule.Strategy ?? "",
                Reason = rule.Reason ?? "",
            };

            if (rule.When is { } when && !TryReadCondition(when, block))
                model.Advanced = true;

            model.Rules.Add(block);
        }

        return model;
    }

    private static bool TryReadCondition(RuleCondition condition, RuleBlock block)
    {
        if (condition.All is null && condition.Any is null)
            return TryReadLeaf(condition, block);

        var children = condition.All ?? condition.Any!;
        block.Join = condition.All is not null ? "all" : "any";

        return children.All(c => c.All is null && c.Any is null)
               && children.All(c => TryReadLeaf(c, block));
    }

    private static bool TryReadLeaf(RuleCondition leaf, RuleBlock block)
    {
        var definition = RuleFacts.Find(leaf.Fact);
        if (definition is null || leaf.Op is null)
            return false;

        block.Conditions.Add(new ConditionRow
        {
            Fact = definition.Fact,
            Op = leaf.Op,
            Value = ValueToText(definition.Kind, leaf.Value),
        });
        return true;
    }

    private static string ValueToText(FactKind kind, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => string.Join(", ",
            value.EnumerateArray().Select(v => ValueToText(kind, v))),
        JsonValueKind.Number when kind == FactKind.Money =>
            (value.GetInt64() / 100m).ToString("0.##", CultureInfo.GetCultureInfo("tr-TR")),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => value.GetString() ?? "",
        _ => "",
    };

    public JsonElement ToJson()
    {
        var root = new JsonObject { ["strategy"] = Strategy };

        var rules = new JsonArray();
        foreach (var block in Rules)
        {
            var rule = new JsonObject();
            if (block.Name is { Length: > 0 })
                rule["name"] = block.Name;

            var leaves = block.Conditions
                .Where(c => c.Value is { Length: > 0 } || c.Definition.Kind == FactKind.Boolean)
                .Select(LeafToJson)
                .ToList();

            if (leaves.Count == 1)
            {
                rule["when"] = leaves[0];
            }
            else if (leaves.Count > 1)
            {
                rule["when"] = new JsonObject { [block.Join] = new JsonArray([.. leaves]) };
            }

            if (block.Route.Count > 0)
                rule["route"] = new JsonArray([.. block.Route.Select(r => (JsonNode)r!)]);
            if (block.Strategy is { Length: > 0 })
                rule["strategy"] = block.Strategy;
            if (block.Reason is { Length: > 0 })
                rule["reason"] = block.Reason;

            rules.Add(rule);
        }

        if (rules.Count > 0)
            root["rules"] = rules;

        var split = Split.Where(s => s.Account is { Length: > 0 } && s.Percent > 0).ToList();
        if (split.Count > 0)
            root["volumeSplit"] = new JsonArray([.. split.Select(s =>
                (JsonNode)new JsonObject { ["account"] = s.Account, ["percent"] = s.Percent })]);

        if (Fallback.Count > 0)
            root["fallback"] = new JsonArray([.. Fallback.Select(f => (JsonNode)f!)]);

        root["guards"] = new JsonObject
        {
            ["skipUnhealthy"] = SkipUnhealthy,
            ["maxAttempts"] = MaxAttempts,
            ["explorePercent"] = ExplorePercent,
        };

        if (Strategy == "balanced" || Rules.Any(r => r.Strategy == "balanced"))
        {
            root["weights"] = new JsonObject
            {
                ["cost"] = CostWeight,
                ["success"] = SuccessWeight,
                ["latency"] = LatencyWeight,
            };
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonNode LeafToJson(ConditionRow row)
    {
        var definition = row.Definition;
        var leaf = new JsonObject { ["fact"] = definition.Fact, ["op"] = row.Op };

        if (row.Op == "in")
        {
            var items = row.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            leaf["value"] = new JsonArray([.. items.Select(i => ScalarToJson(definition.Kind, i))]);
        }
        else
        {
            leaf["value"] = ScalarToJson(definition.Kind, row.Value);
        }

        return leaf;
    }

    private static JsonNode ScalarToJson(FactKind kind, string text) => kind switch
    {
        FactKind.Money => UserInput.ToKurus(text),
        FactKind.Number => long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : 0,
        FactKind.Boolean => !string.Equals(text.Trim(), "false", StringComparison.OrdinalIgnoreCase),
        _ => text.Trim(),
    };
}
