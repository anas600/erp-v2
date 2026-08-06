using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Migration 013 — Auto-link receipts / payments to invoices + tracking
/// payment progress on the invoice itself.
///
/// Sprint 25 (Rules Engine + Auto-link) introduces a small but critical
/// enhancement to the AR/AP sub-ledger workflow: when a user posts a
/// receipt voucher, the system now looks for an unpaid sales invoice
/// from the same customer with the same amount and, if found, marks
/// the invoice as fully or partially paid. Same for payment vouchers
/// against purchase invoices. This migration adds the schema pieces
/// the application code (ReceiptService / PaymentService) needs:
///
///   1. invoices.amount_paid      — running total of payments applied.
///                                   numeric(18,4) to keep precision
///                                   consistent with the invoice total
///                                   (numeric(18,2)) and the 013 spec.
///   2. invoices.paid_at          — when the invoice was fully paid
///                                   (NULL while still partially-paid
///                                   or unpaid).
///   3. invoices.status CHECK     — the schema comment in 003 already
///                                   lists 'paid' as a valid status, but
///                                   there is no actual CHECK constraint
///                                   enforcing it. We add one (idempotent
///                                   via DO $$) so a future bug cannot
///                                   set status = 'pd' or similar typo.
///   4. receipt_vouchers.invoice_id — bi-directional link from the
///                                    payment side back to the invoice
///                                    it satisfied (NULL if the user
///                                    posted a receipt without an
///                                    exact-amount match — most common
///                                    case is a partial deposit).
///   5. payment_vouchers.invoice_id — same, for the AP side.
///   6. amount_paid <= total CHECK  — defence-in-depth. The application
///                                    code already enforces this, but a
///                                    belt-and-braces constraint catches
///                                    out-of-band SQL writes (DBA fixes,
///                                    ad-hoc psql).
///
/// All changes are idempotent (IF NOT EXISTS / DO $$) per the project
/// convention set in 010/011/012 so the migration is safe to re-run
/// on a partially-migrated database.
/// </summary>
[Migration(20260806000013)]
public class AutoLinkInvoicePayment : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1) invoices.amount_paid + paid_at
        // ============================================================
        // amount_paid starts at 0. The application code stamps paid_at
        // when amount_paid first reaches total. We use numeric(18,4)
        // to keep the per-row sum precise even when the user applies
        // partial payments that round differently — the snapshot
        // status is then derived as amount_paid >= total.
        Execute.Sql(@"
            ALTER TABLE invoices
            ADD COLUMN IF NOT EXISTS amount_paid numeric(18,4) NOT NULL DEFAULT 0;
        ");

        Execute.Sql(@"
            ALTER TABLE invoices
            ADD COLUMN IF NOT EXISTS paid_at timestamptz;
        ");

        // Partial index for the ""unpaid invoices for this contact""
        // query that the auto-link logic runs on every receipt / payment
        // post. The filter (amount_paid < total) keeps the index small
        // once most invoices are settled.
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_invoices_unpaid_contact
            ON invoices(company_id, contact_id, total)
            WHERE status NOT IN ('paid', 'cancelled') AND amount_paid < total;
        ");

        // ============================================================
        // 2) invoices.status CHECK (enforce 'paid' as a real status)
        // ============================================================
        // The 003 migration added the column with a comment listing
        // 'paid' as a valid value but no constraint. We add one here
        // so future code can't accidentally set status to a typo.
        // Use NOT VALID so existing data isn't checked at ALTER time
        // (existing data is fine — we just want to enforce going forward).
        // A subsequent VALIDATE CONSTRAINT (with NO VALIDATE) is a no-op
        // for forward-only migrations.
        Execute.Sql(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'chk_invoices_status'
                ) THEN
                    ALTER TABLE invoices
                    ADD CONSTRAINT chk_invoices_status
                    CHECK (status IN ('draft', 'posted', 'paid', 'cancelled'))
                    NOT VALID;
                END IF;
            END $$;
        ");

        // Defence-in-depth: amount_paid cannot exceed total. The
        // auto-link logic in ReceiptService / PaymentService enforces
        // this, but a CHECK catches out-of-band writes (psql, DBA).
        // Use NOT VALID to skip validation of existing data; the
        // application code is the source of truth for new writes.
        Execute.Sql(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'chk_invoices_amount_paid_le_total'
                ) THEN
                    ALTER TABLE invoices
                    ADD CONSTRAINT chk_invoices_amount_paid_le_total
                    CHECK (amount_paid <= total)
                    NOT VALID;
                END IF;
            END $$;
        ");

        // ============================================================
        // 3) receipt_vouchers.invoice_id (FK to invoices)
        // ============================================================
        // Nullable: a receipt may be a general deposit or partial
        // payment that doesn't match any specific invoice. ON DELETE
        // SET NULL keeps the receipt voucher (which is a real cash
        // movement) even if the invoice row is later removed.
        Execute.Sql(@"
            ALTER TABLE receipt_vouchers
            ADD COLUMN IF NOT EXISTS invoice_id uuid;
        ");

        Execute.Sql(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'fk_receipt_vouchers_invoice'
                ) THEN
                    ALTER TABLE receipt_vouchers
                    ADD CONSTRAINT fk_receipt_vouchers_invoice
                    FOREIGN KEY (invoice_id)
                    REFERENCES invoices(id)
                    ON DELETE SET NULL
                    NOT VALID;
                END IF;
            END $$;
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_receipt_vouchers_invoice
            ON receipt_vouchers(invoice_id)
            WHERE invoice_id IS NOT NULL;
        ");

        // ============================================================
        // 4) payment_vouchers.invoice_id (FK to invoices)
        // ============================================================
        // Same shape as the receipt side. Nullable, ON DELETE SET NULL.
        Execute.Sql(@"
            ALTER TABLE payment_vouchers
            ADD COLUMN IF NOT EXISTS invoice_id uuid;
        ");

        Execute.Sql(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'fk_payment_vouchers_invoice'
                ) THEN
                    ALTER TABLE payment_vouchers
                    ADD CONSTRAINT fk_payment_vouchers_invoice
                    FOREIGN KEY (invoice_id)
                    REFERENCES invoices(id)
                    ON DELETE SET NULL
                    NOT VALID;
                END IF;
            END $$;
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_payment_vouchers_invoice
            ON payment_vouchers(invoice_id)
            WHERE invoice_id IS NOT NULL;
        ");
    }

    public override void Down()
    {
        // Forward-only: dropping amount_paid / invoice_id would break
        // the auto-link feature. The Down method is intentionally
        // empty to match the rest of the migrations in this codebase
        // (010/011/012 also leave Down empty).
    }
}
