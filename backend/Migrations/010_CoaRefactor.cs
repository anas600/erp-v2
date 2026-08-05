using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Migration 010 — 4-Level Chart of Accounts Refactor
///
/// The original schema (Migration 001) created a flat list of
/// 18 accounts per company: 1000, 1100, 1200, etc. That worked
/// for a basic chart but doesn't support real accounting needs:
///   - Sub-ledger accounts per customer/supplier (level 4)
///   - Control accounts (1200 AR, 2000 AP) that should not be
///     posted to directly
///   - Hierarchical reporting (e.g. "all current assets")
///
/// This migration adds the columns needed for a 4-level COA
/// WITHOUT breaking the existing 18 accounts. Existing accounts
/// are backfilled with sensible defaults:
///
///   - level: 3 (they're the leaf-level operational accounts
///     that postings go to. The 4-level hierarchy puts them
///     at level 3: Type → Group → Sub-category → Account.
///     Sub-ledger detail accounts for customers/suppliers will
///     be level 4.)
///   - account_class: 'detail' for all 18 (they accept postings).
///   - is_control_account: TRUE for 1200 (AR) and 2000 (AP).
///     Once detail sub-ledger accounts exist, the Posting
///     Engine will reject direct postings to control accounts.
///   - parent_id: SELF-REFERENCING FK for tree structure.
///
/// Idempotency: every ALTER / CREATE uses IF NOT EXISTS. Safe
/// to re-run on a DB where this migration already applied.
/// </summary>
[Migration(20260805000010)]
public class CoaRefactor : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1. Add new columns to accounts
        // ============================================================

        Execute.Sql(@"
            ALTER TABLE accounts
            ADD COLUMN IF NOT EXISTS level int NOT NULL DEFAULT 3;
        ");

        Execute.Sql(@"
            ALTER TABLE accounts
            ADD COLUMN IF NOT EXISTS parent_id uuid;
        ");

        Execute.Sql(@"
            ALTER TABLE accounts
            ADD COLUMN IF NOT EXISTS account_class varchar(20) NOT NULL DEFAULT 'detail';
        ");

        Execute.Sql(@"
            ALTER TABLE accounts
            ADD COLUMN IF NOT EXISTS is_control_account boolean NOT NULL DEFAULT false;
        ");

        Execute.Sql(@"
            ALTER TABLE accounts
            ADD COLUMN IF NOT EXISTS cost_center_required boolean NOT NULL DEFAULT false;
        ");

        // ============================================================
        // 2. Self-referencing FK for parent_id (tree structure)
        // ============================================================

        Execute.Sql(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint WHERE conname = 'fk_accounts_parent'
                ) THEN
                    ALTER TABLE accounts
                    ADD CONSTRAINT fk_accounts_parent
                    FOREIGN KEY (parent_id)
                    REFERENCES accounts(id)
                    ON DELETE SET NULL
                    NOT VALID;
                END IF;
            END $$;
        ");

        // ============================================================
        // 3. Index for tree traversal (parent_id lookups)
        // ============================================================

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_accounts_parent_id
            ON accounts(parent_id);
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_accounts_level
            ON accounts(company_id, level);
        ");

        // ============================================================
        // 4. Backfill existing accounts: 1200 and 2000 are control
        //    accounts (the AR and AP control accounts; the rule
        //    engine should post to sub-ledger accounts, not here)
        // ============================================================

        Execute.Sql(@"
            UPDATE accounts
            SET is_control_account = true
            WHERE code IN ('1200', '2000')
              AND is_control_account = false;
        ");

        // ============================================================
        // 5. account_contact_links: link a level-4 detail account
        //    to a contact (customer or supplier). This is what
        //    makes the sub-ledger queryable: "give me all detail
        //    accounts for customer X" or "all movements on
        //    customer X's sub-account this period".
        // ============================================================

        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS account_contact_links (
                id uuid PRIMARY KEY,
                account_id uuid NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                contact_id uuid NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
                company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
                created_at timestamptz NOT NULL DEFAULT NOW(),
                UNIQUE (account_id),
                UNIQUE (contact_id)
            );
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_acl_contact
            ON account_contact_links(contact_id);
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_acl_company
            ON account_contact_links(company_id);
        ");

        // ============================================================
        // 6. Extend account.code length: existing codes are
        //    4 chars. Sub-ledger codes will be longer
        //    (e.g. '1200-CUST-001'). Bump the column to 50.
        // ============================================================

        Execute.Sql(@"
            ALTER TABLE accounts
            ALTER COLUMN code TYPE varchar(50);
        ");

        // ============================================================
        // 7. cost_centers table for the cost-center feature
        //    (declared here so the schema is in one place even
        //    though the full feature lands in Sprint 23)
        // ============================================================

        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS cost_centers (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
                code varchar(50) NOT NULL,
                name varchar(200) NOT NULL,
                name_ar varchar(200),
                type varchar(20) NOT NULL DEFAULT 'project',
                project_id uuid,
                parent_id uuid REFERENCES cost_centers(id),
                is_active boolean NOT NULL DEFAULT true,
                created_at timestamptz NOT NULL DEFAULT NOW(),
                UNIQUE (company_id, code)
            );
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_cost_centers_company
            ON cost_centers(company_id);
        ");

        // cost_center_id on journal_lines
        Execute.Sql(@"
            ALTER TABLE journal_lines
            ADD COLUMN IF NOT EXISTS cost_center_id uuid REFERENCES cost_centers(id);
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_journal_lines_cost_center
            ON journal_lines(cost_center_id);
        ");
    }

    public override void Down()
    {
        // Forward-only. Rolling back the 4-level COA would
        // orphan the sub-ledger accounts and break the
        // account_contact_links.
    }
}
