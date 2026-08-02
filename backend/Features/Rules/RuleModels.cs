using System.Text.Json.Serialization;

namespace ErpV2.Features.Rules;

public record RuleDto(
    Guid Id,
    string Name,
    string? Description,
    string EventName,
    bool Enabled,
    int Priority,
    string RuleJson,
    bool IsTemplate,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record CreateRuleRequest(
    string Name,
    string? Description,
    string EventName,
    bool Enabled,
    int Priority,
    string RuleJson
);

public record UpdateRuleRequest(
    string Name,
    string? Description,
    string EventName,
    bool Enabled,
    int Priority,
    string RuleJson
);

public record TriggerEventRequest(
    string EventName,
    Dictionary<string, object> Payload
);

/// <summary>
/// JSON-serializable rule definition (stored in business_rules.rule_json).
/// </summary>
public class RuleDefinition
{
    [JsonPropertyName("conditions")]
    public RuleConditionGroup? Conditions { get; set; }

    [JsonPropertyName("actions")]
    public List<RuleAction> Actions { get; set; } = new();
}

public class RuleConditionGroup
{
    [JsonPropertyName("all")]
    public List<RuleCondition> All { get; set; } = new();
}

public class RuleCondition
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    [JsonPropertyName("op")]
    public string Op { get; set; } = "==";

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

public class RuleAction
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("narration")]
    public string? Narration { get; set; }

    [JsonPropertyName("lines")]
    public List<RuleActionLine> Lines { get; set; } = new();
}

public class RuleActionLine
{
    [JsonPropertyName("accountCode")]
    public string AccountCode { get; set; } = "";

    [JsonPropertyName("nature")]
    public string Nature { get; set; } = "debit";

    [JsonPropertyName("amountFormula")]
    public string AmountFormula { get; set; } = "0";

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
