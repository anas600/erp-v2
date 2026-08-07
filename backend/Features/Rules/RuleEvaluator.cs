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
            _log.LogInformation(
                "Rule {RuleId}: line {Code} formula='{Formula}' evaluated to {Amount}",
                ruleId, line.AccountCode, line.AmountFormula, amount);
            if (amount == 0 && line.AmountFormula != "0" && line.AmountFormula != "0.0")
            {
                _log.LogWarning(
                    "Rule {RuleId}: amount formula '{Formula}' evaluated to 0 for account {Code}. " +
                    "Check that the payload contains the referenced field.",
                    ruleId, line.AmountFormula, line.AccountCode);
            }

            // Apply Nature Logic
            var (debit, credit) = _posting.ComputePlacement(account.nature, line.Nature, amount);

            // Sprint 18c — Skip zero-amount lines entirely instead of
            // failing the whole entry. A purchase invoice with no VAT
            // (e.g. tax-exempt goods) would otherwise produce a line
            // with both debit=0 and credit=0, which the journal
            // validator rejects with "Line must have either debit or
            // credit". That rejection used to bubble up to the rule
            // engine's catch and silently mark the rule as having
            // produced zero entries, which made the invoice post
            // endpoint say "no rules enabled for this event" — a
            // confusing and misleading message.
            //
            // The correct behaviour: if a line would have zero
            // amount, drop it. The remaining lines still balance
            // because the rule was authored with the assumption
            // that "subtotal + tax = total" and the other side
            // (the AP line on the purchase rule, or the AR line
            // on the sales rule) is always the full total.
            if (debit == 0 && credit == 0)
            {
                _log.LogInformation(
                    "Rule {RuleId}: skipping zero-amount line for account {Code} (formula '{Formula}' = 0)",
                    ruleId, line.AccountCode, line.AmountFormula);
                continue;
            }

            lines.Add(new CreateJournalLineRequest(
                account.id, debit, credit,
                SubstituteTokens(line.Description ?? "", payload)));
        }

        // If the rule produced only zero-amount lines, that's a
        // real configuration error (e.g. the rule references a
        // payload field that doesn't exist). Surface it loudly
        // rather than silently producing an empty entry that
        // the journal validator will also reject.
        if (lines.Count == 0)
        {
            throw new InvalidOperationException(
                "قاعدة العمل أنتجت قيد فارغ — كل البنود قيمتها صفر. " +
                "الرجاء التحقق من الـ payload ومن صيغ المبالغ في القاعدة.");
        }

        var req = new CreateJournalEntryRequest(
            companyId,
            DateTime.UtcNow,
            SubstituteTokens(action.Narration ?? "", payload),
            lines,
            Source: $"rule:{ruleId}",   // mark the entry as rule-generated for auditing
            RuleId: ruleId
        );

        _log.LogInformation("Rule {RuleId}: creating PENDING journal entry with {Count} lines, source=rule:{RuleId}", ruleId, lines.Count, ruleId);
        // Sprint 15: rule-generated entries start as PENDING (not draft, not posted).
        // The accountant reviews them on the Pending Entries page and approves.
        // This gives the accountant final say over what affects financial reports.
        var entry = await new JournalService(_db, _posting).CreatePendingAsync(req, userId);
        _log.LogInformation("Rule {RuleId}: pending entry {EntryId} created — awaits accountant approval", ruleId, entry.Id);
        return entry;
    }

    private static decimal EvaluateAmount(string formula, Dictionary<string, object> payload)
    {
        // Formula format: "invoice.total" or "invoice.total - invoice.tax" or
        // "{invoice.total}" or "100" or "100 + 50" — anything that DataTable.Compute
        // can evaluate after we substitute payload field references.
        //
        // Two patterns we need to handle:
        //   1. {path.to.value}    — curly-brace placeholder
        //   2. path.to.value      — bare field reference (e.g. "invoice.total")
        //
        // The bare form is the common one in our rules (the seeded templates
        // and the user's "ترحيل فاتورة مبيعات" rule use it). Without the
        // bare-form handling, DataTable.Compute("invoice.total") throws and
        // the catch silently returns 0 — which is exactly why the rules
        // were firing but producing nothing visible.
        var expression = formula;

        // 1) Curly-brace placeholders (legacy support)
        var braceRegex = new System.Text.RegularExpressions.Regex(@"\{([^}]+)\}");
        expression = braceRegex.Replace(expression, match =>
        {
            var v = ResolveField(match.Groups[1].Value.Trim(), payload);
            return ToDecimal(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

        // 2) Bare field references — only identifier.path segments, not
        // operators or numbers. We do a single pass that preserves
        // arithmetic operators by replacing each identifier.match with
        // its value. Order matters: longer paths first so "invoice.total"
        // matches before "invoice".
        var bareRegex = new System.Text.RegularExpressions.Regex(@"\b([a-zA-Z_][a-zA-Z0-9_]*(?:\.[a-zA-Z_][a-zA-Z0-9_]*)+)\b");
        expression = bareRegex.Replace(expression, match =>
        {
            var v = ResolveField(match.Groups[1].Value, payload);
            return ToDecimal(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

        try
        {
            var dt = new System.Data.DataTable();
            return Convert.ToDecimal(dt.Compute(expression, ""));
        }
        catch
        {
            // If evaluation still fails, return 0 — but log it so silent
            // failures don't hide behind "triggered: 0" responses. The
            // caller (RuleEvaluator) will pass through the value as-is.
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
