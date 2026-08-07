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
    /// <summary>
    /// Static account code (e.g. "1103", "1101-CASH-001"). Used when
    /// AccountFrom is empty. For dynamic resolution (e.g. "the contact's
    /// sub-ledger" or "the voucher's bank account"), prefer AccountFrom.
    /// </summary>
    [JsonPropertyName("accountCode")]
    public string AccountCode { get; set; } = "";

    /// <summary>
    /// Sprint 34 — dynamic account resolution. Examples:
    ///   "contact.subLedger"   → use the sub-ledger of the contact on the voucher
    ///   "voucher.bankAccount" → use the bankAccountId from the voucher
    ///   "control.ar"          → use the AR control account (1103)
    ///   "control.ap"          → use the AP control account (2101)
    ///   "control.cash"        → use 1101-CASH-001 (or the bank account on the voucher)
    /// When AccountFrom is set, AccountCode is ignored.
    /// </summary>
    [JsonPropertyName("accountFrom")]
    public string? AccountFrom { get; set; }

    [JsonPropertyName("nature")]
    public string Nature { get; set; } = "debit";

    [JsonPropertyName("amountFormula")]
    public string AmountFormula { get; set; } = "0";

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
