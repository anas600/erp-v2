using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Migration 012 — Intercompany (الشركات الشقيقة) transactions.
///
/// Sprint 24 introduces the multi-company consolidation use case:
/// when HOLD issues a sales invoice to CO-A (its subsidiary), the
/// posting must create TWO journal entries — one in HOLD's books,
/// one in CO-A's books — both with the same amount, opposite party
/// roles (HOLD records CO-A as a customer; CO-A records HOLD as a
/// supplier), and a link identifying them as a pair.
///
/// This migration adds:
///   1. invoices.intercompany_company_id
///        — when set, signals that the invoice must be mirrored into
///          a sister company's books. FK to companies(id).
///   2. intercompany_pairs (new table)
///        — one row per logical intercompany transaction. Holds the
///          primary invoice (in the originating company) and the
///          mirror invoice (in the sister company), the amount, the
///          currency, and the lifecycle status (pending → posted →
///          reversed).
///   3. journal_entries.intercompany_pair_id
///        — back-pointer on each side of the pair. Each half of the
///          pair (HOLD's entry + CO-A's entry) carries the same
///          intercompany_pair_id so a report can pull both rows by
///          one ID without joining through invoices.
///
/// Idempotency: every ALTER and CREATE uses IF NOT EXISTS so the
/// migration is safe to re-run on a partially-migrated database
/// (matches the 010 + 011 pattern in this codebase).
/// </summary>
[Migration(20260805000012)]
public class Intercompany : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1) Add intercompany_company_id to invoices
        // ============================================================
        // Nullable because most invoices are intra-company (single
        // company). When set, the value points at the sister company
        // in whose books a mirror invoice must be created.
        Execute.Sql(@"
            ALTER TABLE invoices
            ADD COLUMN IF NOT EXISTS intercompany_company_id uuid;
        ");

        // FK to companies(id). ON DELETE RESTRICT (the default) so
        // you cannot delete a company that is referenced as a sister
        // by an existing invoice — that would orphan the intercompany
        // pair.
        Execute.Sql(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'fk_invoices_intercompany_company'
                ) THEN
                    ALTER TABLE invoices
                    ADD CONSTRAINT fk_invoices_intercompany_company
                    FOREIGN KEY (intercompany_company_id)
                    REFERENCES companies(id)
                    ON DELETE RESTRICT
                    NOT VALID;
                END IF;
            END $$;
        ");

        // Index for "list all intercompany invoices in company X"
        // (the UI's sister-company filter).
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_invoices_intercompany_company
            ON invoices(intercompany_company_id)
            WHERE intercompany_company_id IS NOT NULL;
        ");

        // ============================================================
        // 2) New table: intercompany_pairs
        // ============================================================
        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS intercompany_pairs (
                id uuid PRIMARY KEY,
                primary_invoice_id uuid NOT NULL UNIQUE,
                mirror_invoice_id uuid NULL UNIQUE,
                primary_company_id uuid NOT NULL,
                mirror_company_id uuid NOT NULL,
                amount numeric(18,4) NOT NULL,
                currency varchar(10) NOT NULL DEFAULT 'LYD',
                status varchar(20) NOT NULL DEFAULT 'pending',
                created_at timestamptz NOT NULL DEFAULT NOW(),
                CONSTRAINT fk_intercompany_pairs_primary_invoice
                    FOREIGN KEY (primary_invoice_id) REFERENCES invoices(id) ON DELETE CASCADE,
                CONSTRAINT fk_intercompany_pairs_mirror_invoice
                    FOREIGN KEY (mirror_invoice_id) REFERENCES invoices(id) ON DELETE CASCADE,
                CONSTRAINT fk_intercompany_pairs_primary_company
                    FOREIGN KEY (primary_company_id) REFERENCES companies(id) ON DELETE RESTRICT,
                CONSTRAINT fk_intercompany_pairs_mirror_company
                    FOREIGN KEY (mirror_company_id) REFERENCES companies(id) ON DELETE RESTRICT,
                CONSTRAINT chk_intercompany_pairs_status
                    CHECK (status IN ('pending', 'posted', 'reversed')),
                CONSTRAINT chk_intercompany_pairs_companies_distinct
                    CHECK (primary_company_id <> mirror_company_id),
                CONSTRAINT chk_intercompany_pairs_amount_positive
                    CHECK (amount > 0)
            );
        ");

        // Index for the "pairs where either side belongs to company X"
        // query — supports both halves of the OR in a single B-tree.
        // PG can use BitmapOr on these two indexes.
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_intercompany_pairs_primary_company
            ON intercompany_pairs(primary_company_id);
        ");
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_intercompany_pairs_mirror_company
            ON intercompany_pairs(mirror_company_id);
        ");
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_intercompany_pairs_status
            ON intercompany_pairs(status, created_at DESC);
        ");

        // ============================================================
        // 3) Add intercompany_pair_id to journal_entries
        // ============================================================
        // Each side of the pair carries the same intercompany_pair_id
        // so a report can fetch both rows in one query. Nullable
        // because most journal entries are intra-company.
        Execute.Sql(@"
            ALTER TABLE journal_entries
            ADD COLUMN IF NOT EXISTS intercompany_pair_id uuid;
        ");

        // FK with ON DELETE SET NULL: if the pair row is removed for
        // any reason, the journal entries themselves stay (they're
        // the authoritative accounting record) and just lose the
        // back-pointer.
        Execute.Sql(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'fk_journal_entries_intercompany_pair'
                ) THEN
                    ALTER TABLE journal_entries
                    ADD CONSTRAINT fk_journal_entries_intercompany_pair
                    FOREIGN KEY (intercompany_pair_id)
                    REFERENCES intercompany_pairs(id)
                    ON DELETE SET NULL
                    NOT VALID;
                END IF;
            END $$;
        ");

        // Index for the elimination report:
        //   "all journal entries in company X that belong to a
        //    posted intercompany pair" → B-tree on (company_id,
        //    intercompany_pair_id).
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_journal_entries_intercompany_pair
            ON journal_entries(company_id, intercompany_pair_id)
            WHERE intercompany_pair_id IS NOT NULL;
        ");
    }

    public override void Down()
    {
        // Forward-only: dropping the intercompany_pair_id FK would
        // break the consolidation use case. The Down method is left
        // intentionally empty to match the rest of the migrations
        // in this codebase.
    }
}
