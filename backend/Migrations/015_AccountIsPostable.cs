using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Migration 015 — is_postable semantic + account_contact_links primary key (Sprint 26).
///
/// The 4-level COA (Migration 010) gave us parent_id, level, account_class,
/// is_control_account, cost_center_required — but the level-based postability
/// rule was implicit: "L4 can post, L1/L2 can't, L3 is up to the user".
/// This migration makes that rule explicit on the column so the Posting Engine,
/// the UI, and the AccountService can all check it in one place.
///
/// New column:
///   - accounts.is_postable        : true = the Posting Engine allows journal
///                                    lines against this account; false =
///                                    pure grouping header.
///                                   Default true (L3/L4 keep their old
///                                   behavior); L1/L2 are forced false.
///                                   L3 is user-overrideable.
///
/// account_contact_links hardening:
///   - is_primary column           : a contact can in theory have multiple
///                                    sub-ledgers (e.g. one for USD and one
///                                    for LYD). is_primary = the canonical
///                                    one. Sprint 26 introduces this column
///                                    so the "primary sub-ledger" lookup is
///                                    a clean partial-index query.
///   - created_at column           : audit trail; matches the pattern on
///                                    every other link table.
///   - ux_account_contact_links_primary
///                                  : partial UNIQUE index. Ensures at
///                                    most one is_primary=true per contact.
///                                    Defends the "primary sub-ledger is
///                                    unique" invariant the EnsureSubLedger
///                                    helper depends on.
///
/// Backfill:
///   - L1/L2 accounts (grouping headers) are stamped is_postable=false.
///     The original 18 seed accounts are L3, so they keep is_postable=true.
///
/// Idempotency: every ALTER / CREATE uses IF NOT EXISTS. The UPDATE uses
/// `WHERE is_postable = true` so re-running is a no-op.
/// </summary>
[Migration(20260805000015)]
public class AccountIsPostable : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1) accounts.is_postable
        // ============================================================
        Execute.Sql(@"
            ALTER TABLE accounts
            ADD COLUMN IF NOT EXISTS is_postable boolean NOT NULL DEFAULT true;
        ");

        // L1/L2 are pure grouping nodes. They never accept postings.
        // L3/L4 stay at the default (true), so existing rows are unaffected.
        Execute.Sql(@"
            UPDATE accounts
            SET is_postable = false
            WHERE level IN (1, 2)
              AND is_postable = true;
        ");

        // ============================================================
        // 2) account_contact_links.is_primary + created_at
        // ============================================================
        // is_primary: defaults to true so existing rows remain "primary".
        // Going forward, EnsureSubLedgerAsync always passes is_primary=true
        // (a contact can have at most one primary sub-ledger at a time).
        Execute.Sql(@"
            ALTER TABLE account_contact_links
            ADD COLUMN IF NOT EXISTS is_primary boolean NOT NULL DEFAULT true;
        ");

        // created_at: just for audit. Defaults to NOW() for any new link.
        Execute.Sql(@"
            ALTER TABLE account_contact_links
            ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT NOW();
        ");

        // ============================================================
        // 3) ux_account_contact_links_primary — at most one primary
        //    sub-ledger per contact.
        // ============================================================
        // Partial UNIQUE index. PostgreSQL supports partial unique
        // indexes natively; we don't need a separate table or trigger.
        //
        // This protects the EnsureSubLedgerAsync invariant: when
        // we auto-create a sub-ledger for a contact that doesn't have
        // one, we set is_primary=true. If a second concurrent request
        // also tries to create one, the index rejects the second
        // INSERT and we get a clean constraint violation rather than
        // two primary sub-ledgers for the same contact.
        Execute.Sql(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ux_account_contact_links_primary
            ON account_contact_links (contact_id)
            WHERE is_primary = true;
        ");
    }

    public override void Down()
    {
        // Forward-only. Dropping is_postable would break the
        // AccountService.IsPostable validation; dropping the unique
        // index would let multiple primary sub-ledgers exist and
        // would break EnsureSubLedgerAsync.
    }
}
