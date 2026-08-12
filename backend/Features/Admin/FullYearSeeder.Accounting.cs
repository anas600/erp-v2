using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Contacts;
using ErpV2.Features.Invoicing;
using ErpV2.Features.Journal;

namespace ErpV2.Features.Admin;

/// <summary>
/// Sprint 40 — Accountant-grade posting for the FullYearSeeder.
///
/// Background: prior sprints used the rules engine to auto-post
/// invoices / receipts / payments to the general ledger. The
/// engine was correct in spirit but it distributed amounts to the
/// *parent* L3 control accounts (e.g. "2101 Accounts Payable")
/// rather than to the *detail* L4 sub-ledger accounts (e.g.
/// "2101-SUPP-007 Sub-ledger: SUPP-007") that the new 4-level
/// chart of accounts requires.
///
/// This file rewrites the posting logic so every transaction is
/// recorded with the correct sub-ledger, the correct cost center,
/// and a balanced journal entry — the way a human accountant would
/// book it. The seeder is the only trusted caller of these
/// helpers; the rest of the system continues to use the standard
/// services (which now refuse to auto-post because the rules are
/// disabled by migration 023).
///
/// Posting conventions implemented here:
///
/// **Sales invoice** (we sell to a customer on credit)
///   Dr  1103-CUST-XXX  (subtotal + VAT)
///   Cr  4101 Sales of Goods  /  4102 Service Revenue  /  4103 Project Revenue
///   Cr  2104 Output VAT Payable
///
/// **Purchase invoice** (we buy from a supplier on credit)
///   Dr  5101-5303 / 5401-5407  (expense / cost of sales)
///   Dr  1107 Input VAT Receivable
///   Cr  2101-SUPP-XXX  (subtotal + VAT)
///
/// **Cash receipt** (customer pays us)
///   Dr  1101-CASH-001 (or 1102-BANK-001 for bank transfers)
///   Cr  1103-CUST-XXX  (the customer's sub-ledger)
///
/// **Cash payment** (we pay a supplier)
///   Dr  2101-SUPP-XXX  (the supplier's sub-ledger)
///   Cr  1101-CASH-001 (or 1102-BANK-001)
///
/// **Project billing** (progress invoice against a contract)
///   Dr  1103-CUST-XXX
///   Cr  4103 Project Revenue
///   Cr  2104 Output VAT Payable
///   (tagged with the project's cost center)
///
/// **Subcontractor cost** (we engage a sub on a project)
///   Dr  5403 Project Subcontractors
///   Dr  1107 Input VAT Receivable
///   Cr  2101-SUPP-XXX
///   (tagged with the project's cost center)
/// </summary>
public partial class FullYearSeeder
{
    // Account codes that the seeder looks up by short-code. These
    // must exist in the standard COA that CoaSeeder plants.
    private const string ACC_CASH        = "1101-CASH-001";
    private const string ACC_BANK        = "1102-BANK-001";
    private const string ACC_INPUT_VAT   = "1107";
    private const string ACC_OUTPUT_VAT  = "2104";
    private const string ACC_SALES       = "4101";
    private const string ACC_SVC_REV     = "4102";
    private const string ACC_PROJ_REV    = "4103";
    private const string ACC_COGS        = "5301";
    private const string ACC_PROJ_MAT    = "5401";
    private const string ACC_PROJ_LAB    = "5402";
    private const string ACC_PROJ_SUB    = "5403";
    private const string ACC_PROJ_EQ     = "5404";
    private const string ACC_PROJ_OVH    = "5405";
    private const string ACC_PROJ_TRN    = "5406";
    private const string ACC_PROJ_OTH    = "5407";

    // ----------------------------------------------------------------
    // Public entry — invoices (sales + purchase)
    // ----------------------------------------------------------------

    /// <summary>
    /// Posts a sales invoice and creates the proper journal entry
    /// using the customer's sub-ledger. Returns the invoice id
    /// (for downstream receipt / payment matching).
    /// </summary>
    private async Task<Guid> PostSalesInvoiceAsync(
        Guid companyId, DateTime invoiceDate, string invoiceNumber,
        string customerCode, string description,
        decimal subtotal, decimal taxAmount, decimal total,
        Guid? projectId, Guid? costCenterId, Guid? userId)
    {
        // 1) Find / create the customer sub-ledger
        if (!_customerIds.TryGetValue(customerCode, out var contactId))
            throw new InvalidOperationException($"Unknown customer {customerCode}");
        var subLedger = await _accounts.GetSubLedgerForContactAsync(companyId, contactId)
            ?? throw new InvalidOperationException(
                $"No sub-ledger for customer {customerCode} — call EnsureSubLedgerAsync first");

        // 2) Build the proper journal entry:
        //    Dr 1103-CUST-XXX (total)  |  Cr 4101 (subtotal)  |  Cr 2104 (VAT)
        var lines = new List<CreateJournalLineRequest>
        {
            new(subLedger.Id, total, 0,
                $"بيع لعميل {customerCode} - {description}", costCenterId),
            new(_accountIds[ACC_SALES], 0, subtotal,
                "إيراد مبيعات سلع", costCenterId),
            new(_accountIds[ACC_OUTPUT_VAT], 0, taxAmount,
                "ضريبة قيمة مضافة مخرجات", costCenterId)
        };

        var jeReq = new CreateJournalEntryRequest(
            companyId, invoiceDate,
            $"فاتورة مبيعات {invoiceNumber} - {customerCode}",
            lines,
            Source: "invoice:sales",
            ProjectId: projectId);
        await CreateAndConditionallyPostAsync(jeReq, userId);
        _result.JournalEntriesCreated++;
        return jeReq == null ? Guid.Empty : Guid.Empty; // not used; caller already has invoice id
    }

    /// <summary>
    /// Posts a purchase invoice and creates the proper journal
    /// entry using the supplier's sub-ledger + input VAT. Returns
    /// the invoice id for downstream payment matching.
    /// </summary>
    private async Task PostPurchaseInvoiceAsync(
        Guid companyId, DateTime invoiceDate, string invoiceNumber,
        string supplierCode, string description,
        decimal subtotal, decimal taxAmount, decimal total,
        string category,  // materials | equipment | services | admin | subcontractor
        Guid? projectId, Guid? costCenterId, Guid? userId)
    {
        if (!_supplierIds.TryGetValue(supplierCode, out var contactId))
            throw new InvalidOperationException($"Unknown supplier {supplierCode}");
        var subLedger = await _accounts.GetSubLedgerForContactAsync(companyId, contactId)
            ?? throw new InvalidOperationException(
                $"No sub-ledger for supplier {supplierCode}");

        // Pick the right expense / cost account by category
        var (expenseAccountCode, expenseDesc) = category switch
        {
            "materials"     => (ACC_COGS,    "تكلفة بضاعة مباعة"),
            "subcontractor" => (ACC_PROJ_SUB, "مقاولي باطن - مشروع"),
            "project_mat"   => (ACC_PROJ_MAT, "مواد مشروع"),
            "project_lab"   => (ACC_PROJ_LAB, "أجور عمال مشروع"),
            "project_eq"    => (ACC_PROJ_EQ,  "تأجير معدات مشروع"),
            "project_trn"   => (ACC_PROJ_TRN, "نقل مشروع"),
            "project_ovh"   => (ACC_PROJ_OVH, "مصاريف عمومية مشروع"),
            "project_oth"   => (ACC_PROJ_OTH, "مصاريف أخرى مشروع"),
            "services"      => ("5201",       "لوازم وخدمات"),
            "admin"         => ("5201",       "لوازم مكتبية"),
            "equipment"     => ("5105",       "صيانة ومعدات"),
            _               => (ACC_COGS,     "مصاريف عامة")
        };

        if (!_accountIds.TryGetValue(expenseAccountCode, out var expenseAccountId))
            throw new InvalidOperationException(
                $"Account {expenseAccountCode} missing from COA — re-run CoaSeeder");

        // Dr expense (subtotal), Dr input VAT, Cr AP sub-ledger (total)
        var lines = new List<CreateJournalLineRequest>
        {
            new(expenseAccountId, subtotal, 0, expenseDesc, costCenterId),
            new(_accountIds[ACC_INPUT_VAT], taxAmount, 0,
                "ضريبة قيمة مضافة مدخلات", costCenterId),
            new(subLedger.Id, 0, total,
                $"مشتريات من {supplierCode} - {description}", costCenterId)
        };

        var jeReq = new CreateJournalEntryRequest(
            companyId, invoiceDate,
            $"فاتورة مشتريات {invoiceNumber} - {supplierCode}",
            lines,
            Source: "invoice:purchase",
            ProjectId: projectId);
        await _journal.CreateAndPostAsync(jeReq, userId);
        _result.JournalEntriesCreated++;
    }

    // ----------------------------------------------------------------
    // Vouchers — receipts and payments already post correctly via
    // the standard services (they build the JE with the right
    // sub-ledger). This helper just adds a cost-center tag for
    // project-related receipts / payments.
    // ----------------------------------------------------------------

    /// <summary>
    /// Wraps the standard ReceiptService.PostAsync but tags the
    /// resulting journal entry with the given cost center (if the
    /// receipt is project-related, e.g. progress collection).
    ///
    /// Implementation note: the standard PostAsync already builds
    /// the correct sub-ledger JE in a single transaction. The only
    /// thing it doesn't support yet is a cost-center tag. We add
    /// that with a follow-up UPDATE — acceptable for a seeder run
    /// because nothing else touches the JE in the same window.
    /// </summary>
    private async Task PostReceiptWithCostCenterAsync(
        Guid receiptId, Guid? costCenterId, Guid? userId)
    {
        await _receipts.PostAsync(receiptId, userId);
        if (costCenterId.HasValue)
        {
            using var conn = _db.CreateConnection();
            await conn.ExecuteAsync(@"
                UPDATE journal_lines
                SET cost_center_id = @ccId
                WHERE journal_entry_id = (
                    SELECT journal_entry_id FROM receipt_vouchers WHERE id = @rId
                )
                  AND account_id IN (SELECT id FROM accounts WHERE code LIKE '1103-%');",
                new { ccId = costCenterId.Value, rId = receiptId });
        }
        _result.JournalEntriesCreated++;
    }

    private async Task PostPaymentWithCostCenterAsync(
        Guid paymentId, Guid? costCenterId, Guid? userId)
    {
        await _payments.PostAsync(paymentId, userId);
        if (costCenterId.HasValue)
        {
            using var conn = _db.CreateConnection();
            await conn.ExecuteAsync(@"
                UPDATE journal_lines
                SET cost_center_id = @ccId
                WHERE journal_entry_id = (
                    SELECT journal_entry_id FROM payment_vouchers WHERE id = @pId
                )
                  AND account_id IN (SELECT id FROM accounts WHERE code LIKE '2101-%');",
                new { ccId = costCenterId.Value, pId = paymentId });
        }
        _result.JournalEntriesCreated++;
    }

    // ----------------------------------------------------------------
    // Project billings (progress invoices)
    // ----------------------------------------------------------------

    /// <summary>
    /// Books a progress billing as:
    ///   Dr 1103-CUST-XXX (gross)        — customer's sub-ledger
    ///   Cr 4103 Project Revenue (net)   — tagged with project CC
    ///   Cr 2104 Output VAT (tax)        — tagged with project CC
    /// </summary>
    private async Task PostProjectBillingAsync(
        Guid companyId, DateTime billingDate, string billingNumber,
        Guid projectId, string customerCode, string description,
        decimal netAmount, decimal taxAmount, decimal grossAmount,
        Guid? costCenterId, Guid? userId)
    {
        if (!_customerIds.TryGetValue(customerCode, out var contactId))
            throw new InvalidOperationException($"Unknown customer {customerCode}");
        var subLedger = await _accounts.GetSubLedgerForContactAsync(companyId, contactId)
            ?? throw new InvalidOperationException(
                $"No sub-ledger for customer {customerCode}");

        var lines = new List<CreateJournalLineRequest>
        {
            new(subLedger.Id, grossAmount, 0,
                $"مستخلص مشروع - {customerCode}", costCenterId),
            new(_accountIds[ACC_PROJ_REV], 0, netAmount,
                "إيراد مشروع", costCenterId),
            new(_accountIds[ACC_OUTPUT_VAT], 0, taxAmount,
                "ضريبة قيمة مضافة مخرجات", costCenterId)
        };

        var jeReq = new CreateJournalEntryRequest(
            companyId, billingDate,
            $"مستخلص {billingNumber} - {customerCode}",
            lines,
            Source: "project:billing",
            ProjectId: projectId);
        await CreateAndConditionallyPostAsync(jeReq, userId);
        _result.JournalEntriesCreated++;
    }

    // ----------------------------------------------------------------
    // Project subcontractor / material costs
    // ----------------------------------------------------------------

    /// <summary>
    /// Books a project cost (subcontractor invoice, materials,
    /// equipment rental, etc.) against the project's cost center.
    /// The supplier's sub-ledger is credited; the matching project
    /// cost account and input VAT are debited.
    /// </summary>
    private async Task PostProjectCostAsync(
        Guid companyId, DateTime costDate, string refNumber,
        Guid projectId, string supplierCode, string description,
        decimal netAmount, decimal taxAmount, decimal grossAmount,
        string costCategory, Guid? costCenterId, Guid? userId)
    {
        var (accCode, accDesc) = costCategory switch
        {
            "subcontractor" => (ACC_PROJ_SUB, "مقاولي باطن"),
            "materials"     => (ACC_PROJ_MAT, "مواد مشروع"),
            "labor"         => (ACC_PROJ_LAB, "أجور عمال"),
            "equipment"     => (ACC_PROJ_EQ,  "تأجير معدات"),
            "transport"     => (ACC_PROJ_TRN, "نقل"),
            "overhead"      => (ACC_PROJ_OVH, "مصاريف عمومية"),
            "other"         => (ACC_PROJ_OTH, "مصاريف أخرى"),
            _               => (ACC_PROJ_OTH, "مصاريف مشروع")
        };

        if (!_accountIds.TryGetValue(accCode, out var costAccountId))
            throw new InvalidOperationException($"Account {accCode} missing");

        if (!_supplierIds.TryGetValue(supplierCode, out var contactId))
            throw new InvalidOperationException($"Unknown supplier {supplierCode}");
        var subLedger = await _accounts.GetSubLedgerForContactAsync(companyId, contactId)
            ?? throw new InvalidOperationException(
                $"No sub-ledger for supplier {supplierCode}");

        var lines = new List<CreateJournalLineRequest>
        {
            new(costAccountId, netAmount, 0, accDesc, costCenterId),
            new(_accountIds[ACC_INPUT_VAT], taxAmount, 0,
                "ضريبة مدخلات مشروع", costCenterId),
            new(subLedger.Id, 0, grossAmount,
                $"مستحقات {supplierCode}", costCenterId)
        };

        var jeReq = new CreateJournalEntryRequest(
            companyId, costDate,
            $"تكلفة مشروع {refNumber} - {supplierCode}",
            lines,
            Source: "project:cost",
            ProjectId: projectId);
        await CreateAndConditionallyPostAsync(jeReq, userId);
        _result.JournalEntriesCreated++;
    }

    // ----------------------------------------------------------------
    // Trusted-accountant gate (Sprint 41)
    // ----------------------------------------------------------------

    /// <summary>
    /// Creates a draft journal entry, then posts it ONLY if the
    /// trusted-accountant mode is enabled. In the strict (default)
    /// mode the JE stays as draft, exactly as a human accountant
    /// would leave it after typing it into the Journal page.
    ///
    /// The split into Create → Approve → Post also makes the
    /// approve/post counters in the seeder result meaningful for
    /// observability — operators can see how many JEs the trusted
    /// path took responsibility for in a given run.
    /// </summary>
    private async Task CreateAndConditionallyPostAsync(
        CreateJournalEntryRequest req, Guid? userId)
    {
        var draft = await _journal.CreateDraftAsync(req, userId);

        if (!TrustedAccountantMode.IsEnabled)
        {
            // Strict path: the seeder writes the draft and stops.
            // The human accountant (or a follow-up auto-approve
            // run) must explicitly approve + post the JE.
            _logger.LogInformation(
                "FullYearSeeder: drafted JE {Number} (strict mode, awaiting accountant review)",
                draft.EntryNumber);
            return;
        }

        // Trusted path: Mavis signs as the accountant.
        var approved = await _journal.ApproveAsync(draft.Id, userId);
        if (approved is null)
            throw new InvalidOperationException(
                $"Auto-approve failed for {draft.EntryNumber} — period may be closed");

        await _journal.PostAsync(draft.Id);
        _result.EntriesApproved++;
        _result.EntriesPosted++;
    }
}
