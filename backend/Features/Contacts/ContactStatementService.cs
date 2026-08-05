using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Invoicing;
using ErpV2.Features.Payments;
using ErpV2.Features.Receipts;

namespace ErpV2.Features.Contacts;

/// <summary>
/// Sprint 25 — Contact Statement (كشف حساب) service.
///
/// Builds the per-contact view that ties invoices, receipts, and
/// payments into a single chronological list with a running balance.
/// This is the page the accountant uses to answer "how much does this
/// customer/supplier currently owe?" without joining three tables in
/// their head.
///
/// Sign convention (running balance):
///   - Customer: positive = customer owes us. Sales invoices add
///     (+total), receipts subtract (-amount). Payments do not appear
///     (customers don't receive payments).
///   - Supplier: positive = we owe supplier. Purchase invoices add
///     (+total), payments subtract (-amount). Receipts do not appear.
///
/// The opening balance is the sum of all (debit - credit) for the
/// contact's sub-ledger account BEFORE the from date. This way the
/// running balance in the period is the true period-end balance and
/// matches the value returned by <c>GetBalanceAsync</c> for any
/// arbitrary as-of date.
/// </summary>
public class ContactStatementService
{
    private readonly IDbConnectionFactory _db;
    private readonly AccountService _accounts;
    private readonly InvoiceService _invoices;
    private readonly ReceiptService _receipts;
    private readonly PaymentService _payments;

    public ContactStatementService(
        IDbConnectionFactory db,
        AccountService accounts,
        InvoiceService invoices,
        ReceiptService receipts,
        PaymentService payments)
    {
        _db = db;
        _accounts = accounts;
        _invoices = invoices;
        _receipts = receipts;
        _payments = payments;
    }

    /// <summary>
    /// List a contact's invoices, filtered by status bucket
    /// ('outstanding' | 'paid' | 'all'). The actual filtering logic
    /// lives in <see cref="InvoiceService.GetByContactAsync"/>.
    /// </summary>
    public async Task<List<ContactInvoiceDto>> GetInvoicesAsync(
        Guid contactId, string status, DateTime asOf)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<(Guid company_id, string type)>(@"
            SELECT company_id, type FROM contacts WHERE id = @id;",
            new { id = contactId });
        if (row.company_id == Guid.Empty) return new List<ContactInvoiceDto>();

        return await _invoices.GetByContactAsync(row.company_id, contactId, status, asOf);
    }

    /// <summary>
    /// Current outstanding balance for a contact, computed as:
    ///   customer: sum(sales_invoices.total - amount_paid) - sum(receipts.amount)
    ///   supplier: sum(purchase_invoices.total - amount_paid) - sum(payments.amount)
    ///
    /// The result is in the contact's natural balance sign (positive =
    /// they owe us / we owe them). For a customer with no transactions
    /// this is 0.
    /// </summary>
    public async Task<ContactBalanceDto> GetBalanceAsync(Guid contactId)
    {
        using var conn = _db.CreateConnection();
        var contact = await conn.QuerySingleOrDefaultAsync<(Guid company_id, string name, string type)>(@"
            SELECT company_id, name, type FROM contacts WHERE id = @id;",
            new { id = contactId });
        if (contact.company_id == Guid.Empty)
            return new ContactBalanceDto(contactId, "", "", 0m, 0m, 0m, 0m);

        // FIX 2026-08-05: contacts.type is 'customer'/'supplier' but
        // invoices.invoice_type is 'sales'/'purchase'. Map correctly.
        var invoiceType = contact.type == "customer" ? "sales" : "purchase";
        var invoiceTotals = await conn.QuerySingleAsync<(decimal total, decimal paid)>(@"
            SELECT
                COALESCE(SUM(i.total), 0)        AS total,
                COALESCE(SUM(i.amount_paid), 0)  AS paid
            FROM invoices i
            WHERE i.company_id = @companyId
              AND i.party_name = @partyName
              AND i.invoice_type = @invoiceType
              AND i.status IN ('posted', 'partiallypaid', 'paid');",
            new
            {
                companyId = contact.company_id,
                partyName = contact.name,
                invoiceType
            });

        decimal voucherTotal = 0m;
        if (contact.type == "customer")
        {
            voucherTotal = await conn.ExecuteScalarAsync<decimal>(@"
                SELECT COALESCE(SUM(r.amount), 0) FROM receipt_vouchers r
                WHERE r.company_id = @companyId
                  AND r.contact_id = @contactId
                  AND r.status = 'posted';",
                new { companyId = contact.company_id, contactId });
        }
        else
        {
            voucherTotal = await conn.ExecuteScalarAsync<decimal>(@"
                SELECT COALESCE(SUM(p.amount), 0) FROM payment_vouchers p
                WHERE p.company_id = @companyId
                  AND p.contact_id = @contactId
                  AND p.status = 'posted';",
                new { companyId = contact.company_id, contactId });
        }

        var outstanding = invoiceTotals.total - invoiceTotals.paid - voucherTotal;
        return new ContactBalanceDto(
            contactId, contact.name, contact.type,
            invoiceTotals.total, invoiceTotals.paid, voucherTotal,
            outstanding);
    }

    /// <summary>
    /// Build a chronological statement for a contact in the given date
    /// range. The opening balance is the contact's sub-ledger balance
    /// BEFORE <paramref name="from"/>; the closing balance is opening
    /// + sum of in-range debits - sum of in-range credits.
    ///
    /// If <paramref name="from"/> is null, the opening balance is
    /// zero (we open "from the beginning of time"). If
    /// <paramref name="to"/> is null, we use today's date.
    /// </summary>
    public async Task<ContactStatementDto?> GetStatementAsync(
        Guid contactId, DateTime? from, DateTime? to)
    {
        using var conn = _db.CreateConnection();
        var contact = await conn.QuerySingleOrDefaultAsync<(Guid company_id, string name, string? name_ar, string type)>(@"
            SELECT company_id, name, name_ar, type FROM contacts WHERE id = @id;",
            new { id = contactId });
        if (contact.company_id == Guid.Empty) return null;

        var fromDate = from?.Date ?? new DateTime(1900, 1, 1);
        var toDate   = (to ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1); // inclusive end-of-day

        // Opening balance from the sub-ledger account (if any). If the
        // contact has no sub-ledger, opening is 0.
        decimal opening = 0m;
        var subLedger = await _accounts.GetSubLedgerForContactAsync(contact.company_id, contactId);
        if (subLedger is not null)
        {
            var raw = await conn.ExecuteScalarAsync<decimal?>(@"
                SELECT COALESCE(SUM(jl.debit - jl.credit), 0)
                FROM journal_lines jl
                JOIN journal_entries je ON je.id = jl.journal_entry_id
                WHERE je.company_id = @companyId
                  AND je.status = 'posted'
                  AND jl.account_id = @accountId
                  AND je.entry_date < @from;",
                new { companyId = contact.company_id, accountId = subLedger.Id, from = fromDate });
            // Sub-ledger accounts are sub-classes of AR (Debit nature) or
            // AP (Credit nature). We need to flip the sign for credit-nature
            // accounts so the running balance is "they owe us" for customers
            // and "we owe them" for suppliers.
            var isDebitNature = subLedger.Nature?.Equals("Debit", StringComparison.OrdinalIgnoreCase) ?? true;
            opening = isDebitNature ? (raw ?? 0) : -(raw ?? 0);
        }

        // Collect all events in the period from three sources. We do
        // three separate queries (one per table) because the shapes
        // are different; merging in SQL would require UNION ALL plus
        // per-row type names. Doing it in C# keeps the SQL portable
        // and the result easy to read.
        var lines = new List<StatementLineRow>();

        // 1) Invoices
        var invoiceRows = await conn.QueryAsync<StatementInvoiceRow>(@"
            SELECT id, invoice_number AS number, invoice_date AS date,
                   invoice_type AS type, total, amount_paid, status
            FROM invoices
            WHERE company_id = @companyId
              AND party_name = @partyName
              AND invoice_date BETWEEN @from AND @to
              AND status IN ('posted', 'partiallypaid', 'paid')
            ORDER BY invoice_date, created_at;",
            new
            {
                companyId = contact.company_id,
                partyName = contact.name,
                from = fromDate,
                to = toDate
            });
        foreach (var i in invoiceRows)
        {
            // For the statement view: the entire invoice total is
            // debited on the invoice date (the receivable/payable
            // was created at that point). Payments are tracked
            // separately so the running balance stays informative
            // even if you only show invoice-level movements.
            lines.Add(new StatementLineRow(
                i.date, "invoice", i.number,
                $"فاتورة {(i.type == "sales" ? "مبيعات" : "مشتريات")} {i.number}",
                Debit: i.total, Credit: 0m,
                RefId: i.id, Status: i.status));
        }

        // 2) Receipts (for customers) or Payments (for suppliers)
        if (contact.type == "customer")
        {
            var receiptRows = await conn.QueryAsync<StatementVoucherRow>(@"
                SELECT id, voucher_number AS number, voucher_date AS date, amount, status
                FROM receipt_vouchers
                WHERE company_id = @companyId
                  AND contact_id = @contactId
                  AND voucher_date BETWEEN @from AND @to
                  AND status = 'posted'
                ORDER BY voucher_date, created_at;",
                new
                {
                    companyId = contact.company_id,
                    contactId,
                    from = fromDate,
                    to = toDate
                });
            foreach (var r in receiptRows)
            {
                lines.Add(new StatementLineRow(
                    r.date, "receipt", r.number,
                    $"سند قبض {r.number}",
                    Debit: 0m, Credit: r.amount,
                    RefId: r.id, Status: r.status));
            }
        }
        else // supplier
        {
            var paymentRows = await conn.QueryAsync<StatementVoucherRow>(@"
                SELECT id, voucher_number AS number, voucher_date AS date, amount, status
                FROM payment_vouchers
                WHERE company_id = @companyId
                  AND contact_id = @contactId
                  AND voucher_date BETWEEN @from AND @to
                  AND status = 'posted'
                ORDER BY voucher_date, created_at;",
                new
                {
                    companyId = contact.company_id,
                    contactId,
                    from = fromDate,
                    to = toDate
                });
            foreach (var p in paymentRows)
            {
                lines.Add(new StatementLineRow(
                    p.date, "payment", p.number,
                    $"سند صرف {p.number}",
                    Debit: 0m, Credit: p.amount,
                    RefId: p.id, Status: p.status));
            }
        }

        // Sort by date (then by the original order which already
        // groups invoice/receipt/payment at the same date). The sort
        // is stable so same-date rows keep their query order.
        var ordered = lines.OrderBy(l => l.Date).ToList();

        // Compute running balance.
        var dtos = new List<StatementLine>();
        decimal running = opening;
        decimal totalDebit = 0m, totalCredit = 0m;
        foreach (var l in ordered)
        {
            running += l.Debit - l.Credit;
            totalDebit += l.Debit;
            totalCredit += l.Credit;
            dtos.Add(new StatementLine(
                l.Date, l.Type, l.Number, l.Description,
                l.Debit, l.Credit, running, l.RefId, l.Status));
        }

        return new ContactStatementDto(
            contactId,
            contact.name,
            contact.name_ar ?? contact.name,
            contact.type,
            fromDate, toDate,
            opening, running, totalDebit, totalCredit,
            dtos);
    }

    private record StatementLineRow(
        DateTime Date, string Type, string Number, string Description,
        decimal Debit, decimal Credit, Guid? RefId, string Status);

    private record StatementInvoiceRow(
        Guid id, string number, DateTime date, string type,
        decimal total, decimal amount_paid, string status);

    private record StatementVoucherRow(
        Guid id, string number, DateTime date, decimal amount, string status);
}

public record ContactBalanceDto(
    Guid ContactId,
    string ContactName,
    string ContactType,             // 'customer' or 'supplier'
    decimal TotalInvoiced,          // sum of invoice totals (posted + partiallypaid + paid)
    decimal TotalPaid,              // sum of amount_paid across those invoices
    decimal TotalVouchers,          // sum of posted receipts (customer) or payments (supplier)
    decimal Outstanding             // invoice_total - invoice_paid - voucher_total; positive = they/we owe
);

public record ContactStatementDto(
    Guid ContactId,
    string ContactName,
    string? ContactNameAr,
    string ContactType,
    DateTime FromDate,
    DateTime ToDate,
    decimal OpeningBalance,
    decimal ClosingBalance,
    decimal TotalDebit,
    decimal TotalCredit,
    List<StatementLine> Lines
);

public record StatementLine(
    DateTime Date,
    string Type,                    // 'invoice' | 'receipt' | 'payment'
    string Number,
    string Description,
    decimal Debit,                  // increases the running balance
    decimal Credit,                 // decreases the running balance
    decimal Balance,                // running balance after this line
    Guid? RefId,                    // invoice or voucher id, for UI navigation
    string Status
);
