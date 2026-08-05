using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Migration 014 — Invoice Settlement + Fiscal Year/Period (Sprint 25).
///
/// Closes the accounting cycle. After this migration, every customer/supplier
/// transaction has a complete lifecycle:
///   1. Created (invoice, draft → posted)
///   2. Settled (receipt/payment voucher optionally linked to a specific invoice)
///   3. Visible (aging reports use outstanding = total - amount_paid)
///   4. Locked (fiscal periods can be closed, journal entries reject posting
///      into a closed period)
///
/// New columns:
///   - invoices.amount_paid        : cumulative paid amount; drives status.
///   - invoices.fully_paid_at      : stamped when status flips to 'paid'.
///   - receipt_vouchers.invoice_id : optional FK to invoices. NULL for advance
///                                    payments; populated when a receipt settles
///                                    a specific invoice.
///   - payment_vouchers.invoice_id : mirror of receipt_vouchers.invoice_id,
///                                    for the supplier side.
///
/// New tables:
///   - fiscal_years  : one row per (company, code). e.g. 2026.
///   - fiscal_periods: 12 monthly periods per fiscal year. Each can be
///                     individually locked to prevent new journal entries
///                     landing in it.
///
/// Indexes:
///   - invoices(contact_id, status)        : "open invoices per contact" query
///   - receipt_vouchers(invoice_id),
///     payment_vouchers(invoice_id)        : back-pointer lookups
///   - fiscal_years(company_id, code)      : "year exists for this company?"
///   - fiscal_periods(fiscal_year_id,
///     period_number)                      : period lookup by year+month
///
/// Seed:
///   - For each existing company, create a fiscal_year for the current UTC
///     year (if not present) and 12 monthly periods. This guarantees the
///     period-lock check has something to look up on a fresh database.
///
/// Idempotency: every CREATE and ALTER uses IF NOT EXISTS. The seed block
/// checks for existence before inserting, so re-running is a no-op.
/// </summary>
[Migration(20260805000014)]
public class InvoiceSettlement : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1) invoices.amount_paid + fully_paid_at
        // ============================================================
        // amount_paid drives the status transitions (Posted ↔ PartiallyPaid ↔ Paid).
        // Default 0 means "no payments yet"; existing rows are consistent.
        Execute.Sql(@"
            ALTER TABLE invoices
            ADD COLUMN IF NOT EXISTS amount_paid numeric(18,2) NOT NULL DEFAULT 0;
        ");

        // fully_paid_at is the audit trail: when did the invoice reach Paid?
        // Nullable because only Paid invoices have a value.
        Execute.Sql(@"
            ALTER TABLE invoices
            ADD COLUMN IF NOT EXISTS fully_paid_at timestamptz;
        ");

        // The invoices table has no contact_id FK (party_name is free text), but
        // there are cases where the user picks a contact from the catalog and
        // the UI sets partyName = c.name. For the "open invoices per contact"
        // query, the existing ix_invoices_company_date is insufficient; the
        // partial index below uses the (company, status) prefix which is what
        // the aging reports and contact-statement page will use most often.
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_invoices_company_status_date
            ON invoices(company_id, status, invoice_date DESC);
        ");

        // ============================================================
        // 2) receipt_vouchers.invoice_id
        // ============================================================
        // Nullable because most receipts are advance payments (no specific
        // invoice being settled). When set, the receipt updates that invoice's
        // amount_paid via InvoiceService.ApplyPaymentAsync.
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
                    ON DELETE RESTRICT
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
        // 3) payment_vouchers.invoice_id
        // ============================================================
        // Mirror of receipt_vouchers.invoice_id, for the supplier side.
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
                    ON DELETE RESTRICT
                    NOT VALID;
                END IF;
            END $$;
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_payment_vouchers_invoice
            ON payment_vouchers(invoice_id)
            WHERE invoice_id IS NOT NULL;
        ");

        // ============================================================
        // 4) fiscal_years table
        // ============================================================
        // One row per (company_id, code). Code is the year as text (e.g. '2026').
        // We store code as varchar rather than int to keep room for non-calendar
        // fiscal years later (e.g. "FY26-Q3") without a schema change.
        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS fiscal_years (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
                code varchar(20) NOT NULL,
                start_date date NOT NULL,
                end_date date NOT NULL,
                is_closed boolean NOT NULL DEFAULT false,
                closed_at timestamptz,
                created_at timestamptz NOT NULL DEFAULT NOW(),
                CONSTRAINT uk_fiscal_years_company_code UNIQUE (company_id, code),
                CONSTRAINT chk_fiscal_years_dates CHECK (end_date >= start_date)
            );
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_fiscal_years_company
            ON fiscal_years(company_id, is_closed);
        ");

        // ============================================================
        // 5) fiscal_periods table
        // ============================================================
        // 12 monthly periods per fiscal year. period_number is 1..12 (Jan..Dec)
        // for calendar years. For non-calendar fiscal years in the future, this
        // would be 1..N where N is the period count; the column is wide enough
        // to handle that without a migration.
        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS fiscal_periods (
                id uuid PRIMARY KEY,
                fiscal_year_id uuid NOT NULL REFERENCES fiscal_years(id) ON DELETE CASCADE,
                period_number int NOT NULL,
                start_date date NOT NULL,
                end_date date NOT NULL,
                is_closed boolean NOT NULL DEFAULT false,
                closed_at timestamptz,
                closed_by uuid REFERENCES users(id),
                created_at timestamptz NOT NULL DEFAULT NOW(),
                CONSTRAINT uk_fiscal_periods_year_number UNIQUE (fiscal_year_id, period_number),
                CONSTRAINT chk_fiscal_periods_number CHECK (period_number BETWEEN 1 AND 36),
                CONSTRAINT chk_fiscal_periods_dates CHECK (end_date >= start_date)
            );
        ");

        // The hot path for the period-lock check in JournalService is:
        //   SELECT is_closed FROM fiscal_periods
        //   WHERE fiscal_year_id IN (SELECT id FROM fiscal_years WHERE company_id = ?)
        //     AND @entryDate::date BETWEEN start_date AND end_date
        // The composite (fiscal_year_id, start_date, end_date) covers it.
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_fiscal_periods_year
            ON fiscal_periods(fiscal_year_id, start_date, end_date);
        ");

        // ============================================================
        // 6) Seed: for each existing company, create a fiscal year for the
        //    current UTC year and 12 monthly periods.
        // ============================================================
        // The seed is idempotent: we only insert if no row exists for
        // (company_id, code). This means re-running the migration is safe.
        //
        // We compute the year boundaries in pure SQL (date_trunc) so the
        // migration is portable and doesn't depend on the app's local time
        // or environment.
        Execute.Sql(@"
            DO $$
            DECLARE
                co_id uuid;
                yr_code text;
                fy_id uuid;
                m int;
                p_start date;
                p_end date;
            BEGIN
                yr_code := to_char(NOW() AT TIME ZONE 'UTC', 'YYYY');

                FOR co_id IN SELECT id FROM companies LOOP
                    -- Skip if a fiscal_year already exists for this company+year
                    IF EXISTS (
                        SELECT 1 FROM fiscal_years
                        WHERE company_id = co_id AND code = yr_code
                    ) THEN
                        CONTINUE;
                    END IF;

                    fy_id := gen_random_uuid();
                    INSERT INTO fiscal_years (id, company_id, code, start_date, end_date, is_closed)
                    VALUES (
                        fy_id,
                        co_id,
                        yr_code,
                        (yr_code || '-01-01')::date,
                        (yr_code || '-12-31')::date,
                        false
                    );

                    -- 12 monthly periods: Jan 1..Jan 31, Feb 1..Feb 28/29, etc.
                    -- We use (yr_code || '-MM-01')::date as start and the last
                    -- day of that month as end. (date_trunc + interval '1 month - 1 day'
                    -- computes the last day of any month cleanly.)
                    FOR m IN 1..12 LOOP
                        p_start := (yr_code || '-' || lpad(m::text, 2, '0') || '-01')::date;
                        p_end   := (date_trunc('month', p_start) + interval '1 month - 1 day')::date;

                        INSERT INTO fiscal_periods (id, fiscal_year_id, period_number, start_date, end_date, is_closed)
                        VALUES (
                            gen_random_uuid(),
                            fy_id,
                            m,
                            p_start,
                            p_end,
                            false
                        );
                    END LOOP;
                END LOOP;
            END $$;
        ");
    }

    public override void Down()
    {
        // Forward-only: dropping fiscal periods or amount_paid would break
        // the settlement logic. The Down method is intentionally empty to
        // match the rest of the migrations in this codebase.
    }
}
