using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 40 — Disable all auto-posting business rules.
///
/// Background: prior sprints seeded "SalesInvoiceApproved",
/// "PurchaseInvoiceApproved", "ReceiptPosted", "PaymentPosted" rules
/// that auto-created journal entries whenever an invoice / receipt /
/// payment was posted. Those rules distribute amounts to the
/// *parent* L3 accounts (e.g. "1103 Accounts Receivable") instead
/// of the customer-specific L4 sub-ledger (e.g. "1103-CUST-007
/// Sub-ledger: CUST-007"). That worked for a flat COA, but our
/// 4-level COA with sub-ledgers per contact requires the *detail*
/// account, so the rules now produce incorrect postings.
///
/// From Sprint 40 onward, the seeder (and any trusted caller) builds
/// the journal entry directly via `JournalService.CreateAndPostAsync`
/// using the proper sub-ledger. The rules engine is parked but kept
/// in the database — the admin can re-enable a single rule at a time
/// from `/api/rules` if a specific business case needs it.
///
/// This migration is a one-way switch in production; the `Down()`
/// only re-enables the rules that this migration disabled, so rolling
/// back restores the prior behaviour.
/// </summary>
[Migration(20260808000023)]
public class DisableAllAutoPostingRules : Migration
{
    public override void Up()
    {
        // Idempotent: only touch rules that are currently enabled.
        // Tag them so Down() can find them again.
        Execute.Sql(@"
            UPDATE business_rules
            SET enabled = false,
                description = CASE
                    WHEN description IS NULL OR description = ''
                        THEN '[disabled by Sprint 40 — manual posting]'
                    ELSE description || ' [disabled by Sprint 40 — manual posting]'
                END,
                updated_at = NOW()
            WHERE enabled = true;
        ");
    }

    public override void Down()
    {
        // Re-enable only the rules that this migration disabled.
        Execute.Sql(@"
            UPDATE business_rules
            SET enabled = true,
                description = REPLACE(description, ' [disabled by Sprint 40 — manual posting]', ''),
                updated_at = NOW()
            WHERE description LIKE '%[disabled by Sprint 40 — manual posting]%';
        ");
    }
}
