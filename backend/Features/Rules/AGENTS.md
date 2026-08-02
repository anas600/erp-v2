# Rules Feature — Business Rules Engine

## Purpose
- Let the client define posting behavior as data, without redeploying code.
- Provide an evaluator that turns events into journal entries by consulting the matching rules.
- Ship a starter set of rule templates the client can copy or modify.

## Ownership
- `RuleModels.cs` — `RuleDto`, `CreateRuleRequest`, `UpdateRuleRequest`, `TriggerEventRequest`, plus the JSON-serializable `RuleDefinition`, `RuleConditionGroup`, `RuleCondition`, `RuleAction`, `RuleActionLine`.
- `RuleService.cs` — CRUD on the `business_rules` table.
- `RuleEvaluator.cs` — runs an event through all enabled matching rules; calls `PostingEngine` for `PostJournalEntry` actions.
- `RuleEndpoints.cs` — `GET/POST/PUT/DELETE /api/rules`, `GET /api/rules?templates=true`, `POST /api/rules/trigger`.

## Local Contracts

### Rule shape (stored as `jsonb` in `business_rules.rule_json`)
```json
{
  "conditions": { "all": [ { "field": "invoice.total", "op": ">", "value": 0 } ] },
  "actions": [
    {
      "type": "PostJournalEntry",
      "narration": "فاتورة مشتريات رقم {invoice.number}",
      "lines": [
        { "accountCode": "5000", "nature": "debit",  "amountFormula": "invoice.total - invoice.tax", "description": "تكلفة" },
        { "accountCode": "2000", "nature": "credit", "amountFormula": "invoice.total",                "description": "دائنون" }
      ]
    }
  ]
}
```

- `conditions` are AND-combined. Empty `all` means "always run".
- `field` supports dot notation: `invoice.total` resolves to `payload.invoice.total`.
- `op` is one of `==`, `!=`, `>`, `>=`, `<`, `<=`, `contains`.
- `amountFormula` supports `{path.to.value}` substitution and simple `+ - * /` arithmetic (via `System.Data.DataTable.Compute`). For complex math, evaluate it client-side and pass a precomputed field.
- The evaluator uses `PostingEngine.ComputePlacement` so contra-accounts (Credit-nature assets) are handled correctly.

### Evaluation flow
1. The caller posts to `POST /api/rules/trigger` with `{ eventName, payload }`.
2. The endpoint resolves the active company from `X-Company-Id` and the user from the token.
3. The evaluator loads all rules where `event_name` matches and `enabled = true`, ordered by `priority ASC`.
4. For each rule, conditions are evaluated against the payload. If all pass, each action runs.
5. `PostJournalEntry` actions become draft entries via `JournalService.CreateDraftAsync` and are immediately posted via `PostingEngine.PostAsync`.
6. Failures in one rule do not stop the others; each rule runs in its own try/catch and logs the error.

### Templates
Six templates ship in the seed migration under `Migrations/002_SeedData.cs`:
- `PurchaseInvoiceApproved` — purchase invoice posting.
- `SalesInvoiceApproved` — sales invoice posting.
- `SupplierPaymentMade` — paying a supplier.
- `CustomerReceiptReceived` — receiving from a customer.
- `PeriodClose` — monthly depreciation entry.
- `ProjectMilestoneCompleted` — project revenue recognition.

Templates are marked `is_template = true`; the UI hides the delete button for them.

## Work Guidance
- Adding a new template type:
  1. Add a row to the seed migration for the template.
  2. Make sure the event name appears in the frontend's `select` in `frontend/src/app/dashboard/rules/page.tsx`.
  3. Document it in `docs/user-guide.md`.
- Adding a new action type: extend `RuleEvaluator.TriggerEventAsync` with a new `if (action.Type == "X")` branch. The current branch is `PostJournalEntry` only.
- Adding a new operator: extend `RuleEvaluator.EvaluateCondition`. Keep the operator string exactly as the JSON expects.
- Rule JSON is parsed on every trigger; the parser is forgiving (skip-and-log on invalid JSON) but the editor in the frontend should validate before saving.

## Verification
- `POST /api/rules/trigger` with a payload that matches a template creates a posted entry.
- The created entry has `source = "rule:<rule-id>"` and its lines are placed by the Posting Engine.
- Disabling a rule (`enabled = false`) prevents it from firing.
- An invalid JSON in `rule_json` is logged and the rule is skipped, not crashing the evaluator.

## Child DOX Index
- *(No child folders; this is a leaf.)*
