using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Rules;

/// <summary>
/// OverdueRuleSeeder — Sprint 25 (Rules Engine + Auto-link).
///
/// The Rules Engine already supports custom rules for the two existing
/// events (SalesInvoiceApproved, PurchaseInvoiceApproved). Sprint 25
/// adds a new event "InvoiceOverdueCheck" that a scheduled job (a
/// cron-driven trigger of the event) is expected to fire once per
/// day. The handler lives in user-configurable rules; this seeder
/// just inserts a *template* rule the user can clone and adjust.
///
/// Template behaviour (when the user enables it):
///   - List all posted sales invoices whose invoice_date is more than
///     30 days old AND status != 'paid' AND amount_paid < total.
///   - For each, write an audit log entry so the user sees a digest
///     in the audit page (no journal entry is created — overdue is
///     a notification, not a financial event).
///
/// The rule's conditions group filters on payload fields the cron
/// job includes:
///   - overdue.invoiceCount   — how many invoices were overdue
///   - overdue.thresholdDays  — usually 30
///
/// The action writes a single audit log row (no PostJournalEntry
/// action; the rule engine today only knows that one action type).
/// The audit row is informational; future sprints may add a
/// "CreateTask" action for a follow-up workflow.
///
/// Why a template:
///   - The user is expected to clone this on the rules page and
///     customise the threshold (30 → 15/45/etc) and the contact
///     filter (a wholesale business may want a different policy
///     than a retail one).
///   - Inserting a template is idempotent: ON CONFLICT (name, event)
///     DO NOTHING, so re-running on startup never duplicates it.
///   - is_template = true so it does NOT fire even if the cron job
///     arrives at the engine — the user must explicitly clone and
///     enable it. This prevents surprise behavior in production.
/// </summary>
public class OverdueRuleSeeder
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<OverdueRuleSeeder> _log;

    public OverdueRuleSeeder(IDbConnectionFactory db, ILogger<OverdueRuleSeeder> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Idempotent. Safe to call from Program.cs on every startup.
    /// Logs a one-line confirmation per insert.
    /// </summary>
    public async Task SeedAsync()
    {
        const string eventName = "InvoiceOverdueCheck";
        const string ruleName  = "تذكير الفواتير المتأخرة (قالب)";

        // The rule_json here is read by RuleEvaluator when the user
        // clones and enables the rule. For now (template only) the
        // engine never runs it, but we keep the structure consistent
        // with the user's expected editing surface.
        //
        // The action is a no-op (type=Log) because the engine today
        // only knows "PostJournalEntry". The actual digest happens
        // inside the scheduled job that triggered the event. When
        // the engine grows a "Log" action type, this template is
        // ready to use without rewriting.
        const string ruleJson = @"{
            ""conditions"": {
                ""all"": [
                    { ""field"": ""overdue.invoiceCount"", ""op"": "">"", ""value"": 0 }
                ]
            },
            ""actions"": [
                {
                    ""type"": ""Log"",
                    ""narration"": ""{overdue.invoiceCount} فاتورة متأخرة السداد منذ أكثر من {overdue.thresholdDays} يوم"",
                    ""lines"": []
                }
            ]
        }";

        using var conn = _db.CreateConnection();
        var existing = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM business_rules
            WHERE name = @ruleName AND event_name = @eventName;",
            new { ruleName, eventName });

        if (existing > 0)
        {
            _log.LogDebug("OverdueRuleSeeder: template rule already present, skipping");
            return;
        }

        await conn.ExecuteAsync(@"
            INSERT INTO business_rules (name, description, event_name, enabled, priority, rule_json, is_template)
            VALUES (@ruleName, @description, @eventName, false, 100, @ruleJson::jsonb, true)
            ON CONFLICT (name, event_name) DO NOTHING;",
            new
            {
                ruleName,
                description = "قالب جاهز للفواتير المتأخرة — استنسخه من صفحة قواعد العمل وفعّله",
                eventName,
                ruleJson
            });

        _log.LogInformation(
            "OverdueRuleSeeder: inserted template rule '{Rule}' for event {Event}",
            ruleName, eventName);
    }
}
