using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Migration 011 — Receipt Vouchers + Payment Vouchers
///
/// A receipt voucher (سند قبض) is a document that records
/// money received from a customer. A payment voucher (سند صرف)
/// records money paid to a supplier. Both are draft-then-post
/// workflows: the user fills the form, saves as draft, posts
/// to generate a journal entry, the accountant approves.
///
/// The journal entry for a receipt voucher is:
///   DR Cash/Bank (1000/1100)  = amount
///   CR AR sub-ledger for customer = amount
///
/// The journal entry for a payment voucher is:
///   DR AP sub-ledger for supplier = amount
///   CR Cash/Bank (1000/1100)  = amount
///
/// Both tables are created in this single migration because
/// they're symmetric and small.
/// </summary>
[Migration(20260805000011)]
public class ReceiptAndPaymentVouchers : Migration
{
    public override void Up()
    {
        // ============================================================
        // Receipt Vouchers (سندات القبض)
        // ============================================================
        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS receipt_vouchers (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
                voucher_number varchar(50) UNIQUE NOT NULL,
                voucher_date date NOT NULL,
                contact_id uuid NOT NULL REFERENCES contacts(id),
                amount decimal(18,2) NOT NULL,
                payment_method varchar(20) NOT NULL DEFAULT 'cash',
                bank_account_id uuid REFERENCES accounts(id),
                check_number varchar(50),
                check_date date,
                reference varchar(200),
                narration text,
                status varchar(20) NOT NULL DEFAULT 'draft',
                posted_at timestamptz,
                journal_entry_id uuid REFERENCES journal_entries(id),
                created_by uuid REFERENCES users(id),
                created_at timestamptz NOT NULL DEFAULT NOW(),
                CHECK (amount > 0),
                CHECK (status IN ('draft', 'posted', 'void'))
            );
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_receipts_company
            ON receipt_vouchers(company_id);
        ");
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_receipts_contact
            ON receipt_vouchers(contact_id);
        ");
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_receipts_status
            ON receipt_vouchers(company_id, status);
        ");

        // ============================================================
        // Payment Vouchers (سندات الصرف)
        // ============================================================
        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS payment_vouchers (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
                voucher_number varchar(50) UNIQUE NOT NULL,
                voucher_date date NOT NULL,
                contact_id uuid NOT NULL REFERENCES contacts(id),
                amount decimal(18,2) NOT NULL,
                payment_method varchar(20) NOT NULL DEFAULT 'cash',
                bank_account_id uuid REFERENCES accounts(id),
                check_number varchar(50),
                check_date date,
                reference varchar(200),
                narration text,
                status varchar(20) NOT NULL DEFAULT 'draft',
                posted_at timestamptz,
                journal_entry_id uuid REFERENCES journal_entries(id),
                created_by uuid REFERENCES users(id),
                created_at timestamptz NOT NULL DEFAULT NOW(),
                CHECK (amount > 0),
                CHECK (status IN ('draft', 'posted', 'void'))
            );
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_payments_company
            ON payment_vouchers(company_id);
        ");
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_payments_contact
            ON payment_vouchers(contact_id);
        ");
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_payments_status
            ON payment_vouchers(company_id, status);
        ");
    }

    public override void Down()
    {
        // Forward-only.
    }
}
