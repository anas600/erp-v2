using System.Text.Json;
using Dapper;
using ErpV2.Common;
using ErpV2.Features.Journal;

namespace ErpV2.Features.Rules;

/// <summary>
/// Rule Evaluator — applies business rules when events are dispatched.
///
/// Flow:
///   1. Event arrives (e.g. "InvoiceApproved" with payload)
///   2. Load all enabled rules for that event
///   3. For each rule (sorted by priority):
///       a. Evaluate conditions against the payload
///       b. If conditions met, execute actions
///       c. For "PostJournalEntry" actions, use PostingEngine to create the entry
///   4. Return list of created entries
/// </summary>
public class RuleEvaluator
{
    private readonly IDbConnectionFactory _db;
    private readonly PostingEngine _posting;
    private readonly ILogger<RuleEvaluator> _log;

    public RuleEvaluator(IDbConnectionFactory db, PostingEngine posting, ILogger<RuleEvaluator> log)
    {
        _db = db;
        _posting = posting;
        _log = log;
    }

    /// <summary>
    /// Triggers an event and runs all matching rules in priority order.
    ///
    /// Each rule may produce one or more journal entries via the Posting Engine. A failure
    /// in one rule is logged and the loop continues to the next rule; one bad rule will
    /// not block the others.
    ///
    /// The active company comes from the HTTP context (caller's responsibility); the user
    /// id is optional and used to stamp `created_by` on created entries.
    /// </summary>
    /// <param name="companyId">The company in whose scope the event runs.</param>
    /// <param name="userId">The triggering user (optional; null for system-triggered events).</param>
    /// <param name="eventName">The event name matched against <c>business_rules.event_name</c>.</param>
    /// <param name="payload">The event payload, accessible via dot-notation in conditions and formulas.</param>
    /// <returns>The list of journal entries created by all matching rules.</returns>
    public async Task<List<JournalEntryDto>> TriggerEventAsync(Guid companyId, Guid? userId, string eventName, Dictionary<string, object> payload)
    {
        var results = new List<JournalEntryDto>();

        using var conn = _db.CreateConnection();
        var rules = (await conn.QueryAsync<RuleRow>(@"
            SELECT id, name, description, event_name, enabled, priority, rule_json::text, is_template, created_at, updated_at
            FROM business_rules
            WHERE event_name = @eventName AND enabled = true
            ORDER BY priority ASC;",
            new { eventName })).ToList();

        _log.LogInformation("Triggering event {Event} for company {Company} — {Count} matching rule(s)", eventName, companyId, rules.Count);

        foreach (var rule in rules)
        {
            try
            {
                RuleDefinition? def;
                try
                {
                    def = JsonSerializer.Deserialize<RuleDefinition>(rule.rule_json);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Rule {RuleId} has invalid JSON, skipping", rule.id);
                    continue;
                }
                if (def is null) continue;

                // Evaluate conditions
                if (!EvaluateConditions(def.Conditions, payload))
                {
                    _log.LogDebug("Rule {RuleId} conditions not met, skipping", rule.id);
                    continue;
                }

                // Execute actions
                foreach (var action in def.Actions)
                {
                    if (action.Type == "PostJournalEntry")
                    {
                        var entry = await ExecutePostJournalEntry(companyId, userId, rule.id, action, payload);
                        results.Add(entry);
                    }
                    else
                    {
                        _log.LogWarning("Unknown action type: {Type}", action.Type);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error executing rule {RuleId}", rule.id);
            }
        }

        return results;
    }

    private static bool EvaluateConditions(RuleConditionGroup? group, Dictionary<string, object> payload)
    {
        if (group is null || group.All.Count == 0) return true;

        foreach (var cond in group.All)
        {
            if (!EvaluateCondition(cond, payload)) return false;
        }
        return true;
    }

    private static bool EvaluateCondition(RuleCondition cond, Dictionary<string, object> payload)
    {
        // Support dot-notation: "invoice.total" → payload["invoice"]["total"]
        var actualValue = ResolveField(cond.Field, payload);
        var expectedValue = cond.Value;

        return cond.Op switch
        {
            "==" or "=" => Equals(Normalize(actualValue), Normalize(expectedValue)),
            "!=" or "<>" => !Equals(Normalize(actualValue), Normalize(expectedValue)),
            ">" => CompareNumeric(actualValue, expectedValue) > 0,
            ">=" => CompareNumeric(actualValue, expectedValue) >= 0,
            "<" => CompareNumeric(actualValue, expectedValue) < 0,
            "<=" => CompareNumeric(actualValue, expectedValue) <= 0,
            "contains" => actualValue?.ToString()?.Contains(expectedValue?.ToString() ?? "") ?? false,
            _ => false
        };
    }

    private static object? ResolveField(string field, Dictionary<string, object> payload)
    {
        var parts = field.Split('.');
        object? current = payload;
        foreach (var part in parts)
        {
            if (current is Dictionary<string, object> dict && dict.TryGetValue(part, out var v))
                current = v;
            else if (current is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Object && je.TryGetProperty(part, out var prop))
                    current = JsonElementToObject(prop);
                else return null;
            }
            else
                return null;
        }
        return current;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.ToString()
    };

    private static int CompareNumeric(object? a, object? b)
    {
        decimal da = ToDecimal(a), db = ToDecimal(b);
        return da.CompareTo(db);
    }

    private static decimal ToDecimal(object? v)
    {
        if (v is null) return 0;
        if (v is decimal d) return d;
        if (v is int i) return i;
        if (v is long l) return l;
        if (v is double db) return (decimal)db;
        if (decimal.TryParse(v.ToString(), out var r)) return r;
        return 0;
    }

    private static object? Normalize(object? v) => v switch
    {
        null => null,
        JsonElement je => JsonElementToObject(je),
        _ => v
    };

    /// <summary>
    /// Executes a "PostJournalEntry" rule action: resolves the accounts, evaluates
    /// the amount formulas, applies the Nature Logic, then drafts and posts the entry
    /// through the regular Journal + Posting Engine pipeline.
    ///
    /// The account code in the rule is resolved against the active company's chart of
    /// accounts. Amount formulas may reference payload fields with `{path.to.value}`
    /// substitution plus simple `+ - * /` arithmetic.
    /// </summary>
    private async Task<JournalEntryDto> ExecutePostJournalEntry(
        Guid companyId, Guid? userId, Guid ruleId, RuleAction action, Dictionary<string, object> payload)
    {
        using var conn = _db.CreateConnection();

        var lines = new List<CreateJournalLineRequest>();

        foreach (var line in action.Lines)
        {
            // Resolve account by code (within the company)
            var account = await conn.QuerySingleOrDefaultAsync<(Guid id, string nature)>(@"
                SELECT id, nature FROM accounts
                WHERE company_id = @companyId AND code = @code AND is_active = true
                LIMIT 1;",
                new { companyId, code = line.AccountCode });

            if (account.id == Guid.Empty)
                throw new InvalidOperationException($"Account with code '{line.AccountCode}' not found in this company");

            // Evaluate amount formula (simple {path.to.value} substitution)
            var amount = EvaluateAmount(line.AmountFormula, payload);

            // Apply Nature Logic
            var (debit, credit) = _posting.ComputePlacement(account.nature, line.Nature, amount);

            lines.Add(new CreateJournalLineRequest(
                account.id, debit, credit,
                SubstituteTokens(line.Description ?? "", payload)));
        }

        var req = new CreateJournalEntryRequest(
            companyId,
            DateTime.UtcNow,
            SubstituteTokens(action.Narration ?? "", payload),
            lines
        );

        var entry = await new JournalService(_db, _posting).CreateDraftAsync(req, userId);
        // Auto-post
        return await _posting.PostAsync(entry.Id);
    }

    private static decimal EvaluateAmount(string formula, Dictionary<string, object> payload)
    {
        // Simple formula: supports + - * / and {path.to.value} substitution
        var expression = formula;
        var stack = new Stack<string>();
        // Replace {path.to.value} with numeric values
        var regex = new System.Text.RegularExpressions.Regex(@"\{([^}]+)\}");
        expression = regex.Replace(formula, match =>
        {
            var v = ResolveField(match.Groups[1].Value, payload);
            return ToDecimal(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
        try
        {
            // Tiny safe expression evaluator using NCalc-style would be ideal,
            // but for MVP we support simple arithmetic only
            var dt = new System.Data.DataTable();
            return Convert.ToDecimal(dt.Compute(expression, ""));
        }
        catch
        {
            return 0;
        }
    }

    private static string SubstituteTokens(string template, Dictionary<string, object> payload)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var regex = new System.Text.RegularExpressions.Regex(@"\{([^}]+)\}");
        return regex.Replace(template, match =>
        {
            var v = ResolveField(match.Groups[1].Value, payload);
            return v?.ToString() ?? "";
        });
    }

    private record RuleRow(
        Guid id, string name, string? description, string event_name, bool enabled,
        int priority, string rule_json, bool is_template, DateTime created_at, DateTime updated_at);
}
