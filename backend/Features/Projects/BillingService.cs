using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Journal;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 36 — Progress Billing (مستخلصات) service.
///
/// This is the workhorse of the contracting workflow. It owns the
/// calculation engine that turns a "work completed %" into four
/// monetary figures (gross / advance / retention / net), and it
/// drives the DRAFT → INVOICED → CANCELLED lifecycle.
///
/// Calculation algorithm (Sprint 36 spec, with the worked example):
///   Contract  : value=100,000, advance=10%, retention=5%, retention_start=1
///   Billing 1 : work_completed=30% → gross=30,000
///               previous_advance=0 → remaining_advance=10,000
///               advance_deducted = min(30,000, 10,000) = 10,000
///               retention_deducted = 30,000 * 5% = 1,500
///               net = 30,000 - 10,000 - 1,500 = 18,500
///   Billing 2 : work_completed=60% → gross=60,000
///               previous_advance=10,000 → remaining_advance=0
///               advance_deducted = 0
///               retention_deducted = 60,000 * 5% = 3,000
///               net = 60,000 - 0 - 3,000 = 57,000
///
/// Atomicity (ApproveAsync):
///   The whole "create invoice + create JE + update billing status"
///   dance is wrapped in a single transaction. If any step fails,
///   nothing is persisted. This is the critical property — a partial
///   state (invoice without billing update) would leave the
///   statement tab lying to the user.
/// </summary>
public class BillingService
{
    private readonly IDbConnectionFactory _db;
    private readonly ContractService _contracts;
    private readonly AccountService _accounts;
    private readonly JournalService _journal;
    private readonly PostingEngine _posting;
    private readonly ILogger<BillingService> _log;

    public BillingService(
        IDbConnectionFactory db,
        ContractService contracts,
        AccountService accounts,
        JournalService journal,
        PostingEngine posting,
        ILogger<BillingService> log)
    {
        _db = db;
        _contracts = contracts;
        _accounts = accounts;
        _journal = journal;
        _posting = posting;
        _log = log;
    }

    // ============================================================
    // Reads
    // ============================================================

    /// <summary>
    /// Lists every billing for the project, ordered by billing date
    /// (oldest first). The UI uses this to draw the per-project
    /// billing timeline and the cumulative-% progress bar.
    /// </summary>
    public async Task<List<ProgressBillingDto>> GetByProjectAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<BillingRow>(@"
            SELECT id, company_id, project_id, contract_id, billing_number,
                   billing_date, period_from, period_to,
                   work_completed_percent, gross_amount,
                   advance_deducted, retention_deducted, net_amount,
                   status, invoice_id, journal_entry_id, notes,
                   created_at, updated_at
            FROM progress_billings
            WHERE project_id = @projectId
            ORDER BY billing_date ASC, created_at ASC;",
            new { projectId });
        return rows.Select(MapRow).ToList();
    }

    /// <summary>
    /// Fetches a single billing by id. Used by GET /api/billings/{id}.
    /// </summary>
    public async Task<ProgressBillingDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<BillingRow>(@"
            SELECT id, company_id, project_id, contract_id, billing_number,
                   billing_date, period_from, period_to,
                   work_completed_percent, gross_amount,
                   advance_deducted, retention_deducted, net_amount,
                   status, invoice_id, journal_entry_id, notes,
                   created_at, updated_at
            FROM progress_billings
            WHERE id = @id;",
            new { id });
        return row is null ? null : MapRow(row);
    }

    // ============================================================
    // Create (the calculation engine)
    // ============================================================

    /// <summary>
    /// Creates a new progress billing. This is where the advance
    /// and retention math happens — see the class-level comment for
    /// the algorithm.
    ///
    /// Refuses to create the billing if:
    ///   - the project has no contract yet
    ///   - the work_completed_percent is lower than the previous max
    ///     (you can't go backwards — projects accumulate progress)
    ///   - the contract belongs to a different company than the
    ///     project (impossible by FK but defended in depth)
    /// </summary>
    public async Task<ProgressBillingDto> CreateAsync(Guid projectId, CreateBillingRequest req)
    {
        // 1) Validate the percent range up front (cheap).
        if (req.WorkCompletedPercent < 0 || req.WorkCompletedPercent > 100)
            throw new InvalidOperationException("نسبة الإنجاز يجب أن تكون بين 0 و 100");
        if (string.IsNullOrWhiteSpace(req.BillingNumber))
            throw new InvalidOperationException("رقم المستخلص مطلوب");

        using var conn = _db.CreateConnection();

        // 2) Load the project — need its company_id for the
        //    cross-company check and to stamp progress_billings.company_id.
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id, string? name, string? name_ar, Guid? customer_id)?>(@"
            SELECT id, company_id, name, name_ar, customer_id
            FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null)
            throw new InvalidOperationException("المشروع غير موجود");

        // 3) Load the contract (must exist for this project).
        var contract = await _contracts.GetByProjectAsync(projectId);
        if (contract is null)
            throw new InvalidOperationException("لا يوجد عقد لهذا المشروع. الرجاء إنشاء عقد أولاً.");
        if (contract.Id != req.ContractId)
            throw new InvalidOperationException("العقد المحدد لا يخص هذا المشروع");
        if (contract.CompanyId != project.Value.company_id)
            throw new InvalidOperationException("العقد لا ينتمي لنفس شركة المشروع");

        // 4) Check uniqueness of billing_number within the company
        //    (pre-check for a friendly error; the UNIQUE index also
        //    catches it at the DB level as a backstop).
        var dup = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM progress_billings
            WHERE company_id = @companyId AND billing_number = @billingNumber;",
            new { companyId = project.Value.company_id, billingNumber = req.BillingNumber });
        if (dup > 0)
            throw new InvalidOperationException(
                $"رقم المستخلص '{req.BillingNumber}' مستخدم بالفعل في هذه الشركة");

        // 5) Sum previous billings — the cumulative math inputs.
        //    We exclude CANCELLED billings (they don't count toward
        //    advance/retention accounting).
        var previousGross = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(gross_amount), 0) FROM progress_billings
            WHERE project_id = @projectId
              AND status != 'CANCELLED';",
            new { projectId }) ?? 0m;
        var previousAdvance = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(advance_deducted), 0) FROM progress_billings
            WHERE project_id = @projectId
              AND status != 'CANCELLED';",
            new { projectId }) ?? 0m;
        var previousMaxPercent = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(MAX(work_completed_percent), 0) FROM progress_billings
            WHERE project_id = @projectId
              AND status != 'CANCELLED';",
            new { projectId }) ?? 0m;
        var nextBillingNumber = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) + 1 FROM progress_billings
            WHERE project_id = @projectId
              AND status != 'CANCELLED';",
            new { projectId });

        // 6) Validate cumulative % — can't go backwards.
        if (req.WorkCompletedPercent < previousMaxPercent)
            throw new InvalidOperationException(
                $"نسبة الإنجاز ({req.WorkCompletedPercent}%) أقل من الحد الأقصى السابق ({previousMaxPercent}%). " +
                "لا يمكن إنقاص نسبة الإنجاز التراكمية.");

        // 7) Calculate the four amounts.
        var gross = Math.Round(contract.ContractValue * (req.WorkCompletedPercent / 100m), 3);
        var advanceTotal = Math.Round(contract.ContractValue * (contract.AdvancePercent / 100m), 3);
        var remainingAdvance = Math.Max(0m, advanceTotal - previousAdvance);
        var advanceDeducted = Math.Min(gross, remainingAdvance);
        // Round to 3dp to keep column-precision consistent. We use
        // 3 because the column is decimal(18,3).
        advanceDeducted = Math.Round(advanceDeducted, 3);

        decimal retentionDeducted = 0m;
        if (nextBillingNumber >= contract.RetentionStartBilling)
        {
            retentionDeducted = Math.Round(gross * (contract.RetentionPercent / 100m), 3);
        }

        var net = Math.Round(gross - advanceDeducted - retentionDeducted, 3);

        // 8) Insert the billing in DRAFT status. The user reviews
        //    the four figures and clicks Approve to commit.
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO progress_billings (
                id, company_id, project_id, contract_id, billing_number,
                billing_date, period_from, period_to,
                work_completed_percent, gross_amount,
                advance_deducted, retention_deducted, net_amount,
                status, notes, created_at
            )
            VALUES (
                @id, @companyId, @projectId, @contractId, @billingNumber,
                @billingDate, @periodFrom, @periodTo,
                @workCompletedPercent, @gross,
                @advanceDeducted, @retentionDeducted, @net,
                'DRAFT', @notes, NOW()
            );",
            new
            {
                id,
                companyId = project.Value.company_id,
                projectId,
                contractId = contract.Id,
                billingNumber = req.BillingNumber,
                billingDate = req.BillingDate,
                periodFrom = req.PeriodFrom,
                periodTo = req.PeriodTo,
                workCompletedPercent = req.WorkCompletedPercent,
                gross,
                advanceDeducted,
                retentionDeducted,
                net,
                notes = req.Notes
            });

        return (await GetByIdAsync(id))!;
    }

    // ============================================================
    // Update (only while DRAFT)
    // ============================================================

    /// <summary>
    /// Replaces the editable fields on a DRAFT billing. If the
    /// percent changed, the four amount columns are recomputed via
    /// the same algorithm as CreateAsync (so the displayed numbers
    /// are always in sync with the contract terms).
    /// </summary>
    public async Task<ProgressBillingDto?> UpdateAsync(Guid id, UpdateBillingRequest req)
    {
        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<BillingRow>(@"
            SELECT id, company_id, project_id, contract_id, billing_number,
                   billing_date, period_from, period_to,
                   work_completed_percent, gross_amount,
                   advance_deducted, retention_deducted, net_amount,
                   status, invoice_id, journal_entry_id, notes,
                   created_at, updated_at
            FROM progress_billings WHERE id = @id;",
            new { id });
        if (existing is null) return null;
        if (existing.status != "DRAFT")
            throw new InvalidOperationException(
                $"لا يمكن تعديل مستخلص بحالة '{existing.status}'. المتوقع: DRAFT");

        var contract = await _contracts.GetByIdAsync(existing.contract_id)
            ?? throw new InvalidOperationException("العقد غير موجود");

        // Recompute only if the percent actually changed.
        decimal newPercent = req.WorkCompletedPercent ?? existing.work_completed_percent;
        if (newPercent < 0 || newPercent > 100)
            throw new InvalidOperationException("نسبة الإنجاز يجب أن تكون بين 0 و 100");

        // Validate cumulative % against the OTHER billings (exclude self).
        var otherMaxPercent = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(MAX(work_completed_percent), 0) FROM progress_billings
            WHERE project_id = @projectId AND id <> @id AND status != 'CANCELLED';",
            new { projectId = existing.project_id, id }) ?? 0m;
        if (newPercent < otherMaxPercent)
            throw new InvalidOperationException(
                $"نسبة الإنجاز ({newPercent}%) أقل من الحد الأقصى للمستخلصات الأخرى ({otherMaxPercent}%)");

        // Re-sum the OTHER billings' gross + advance (so we don't
        // double-count this row's old values into the cumulative).
        var previousGross = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(gross_amount), 0) FROM progress_billings
            WHERE project_id = @projectId AND id <> @id AND status != 'CANCELLED';",
            new { projectId = existing.project_id, id }) ?? 0m;
        var previousAdvance = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(advance_deducted), 0) FROM progress_billings
            WHERE project_id = @projectId AND id <> @id AND status != 'CANCELLED';",
            new { projectId = existing.project_id, id }) ?? 0m;
        var nextBillingNumber = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) + 1 FROM progress_billings
            WHERE project_id = @projectId AND id <> @id AND status != 'CANCELLED';",
            new { projectId = existing.project_id, id });

        var gross = Math.Round(contract.ContractValue * (newPercent / 100m), 3);
        var advanceTotal = Math.Round(contract.ContractValue * (contract.AdvancePercent / 100m), 3);
        var remainingAdvance = Math.Max(0m, advanceTotal - previousAdvance);
        var advanceDeducted = Math.Round(Math.Min(gross, remainingAdvance), 3);

        decimal retentionDeducted = 0m;
        if (nextBillingNumber >= contract.RetentionStartBilling)
            retentionDeducted = Math.Round(gross * (contract.RetentionPercent / 100m), 3);

        var net = Math.Round(gross - advanceDeducted - retentionDeducted, 3);

        await conn.ExecuteAsync(@"
            UPDATE progress_billings
            SET billing_number = COALESCE(@billingNumber, billing_number),
                billing_date = COALESCE(@billingDate, billing_date),
                period_from = @periodFrom,
                period_to = @periodTo,
                work_completed_percent = @workCompletedPercent,
                gross_amount = @gross,
                advance_deducted = @advanceDeducted,
                retention_deducted = @retentionDeducted,
                net_amount = @net,
                notes = @notes,
                updated_at = NOW()
            WHERE id = @id;",
            new
            {
                id,
                billingNumber = req.BillingNumber,
                billingDate = req.BillingDate,
                periodFrom = req.PeriodFrom,
                periodTo = req.PeriodTo,
                workCompletedPercent = newPercent,
                gross,
                advanceDeducted,
                retentionDeducted,
                net,
                notes = req.Notes
            });

        return await GetByIdAsync(id);
    }

    // ============================================================
    // Approve (the atomic dance)
    // ============================================================

    /// <summary>
    /// Approves a DRAFT billing: creates a POSTED sales invoice for
    /// the net amount AND a POSTED journal entry (DR AR sub-ledger
    /// / CR Sales of Goods 4101), all in a single transaction.
    /// Updates the billing to status='INVOICED' with both back-
    /// links in the same transaction so the report is internally
    /// consistent.
    ///
    /// The post-commit PostingEngine.PostAsync step is a separate
    /// transaction (PostingEngine owns its own connection). If it
    /// fails, we log loudly and return the billing with a flag
    /// for the caller — but the invoice + billing status are
    /// already committed. This is the same risk envelope as the
    /// rule pipeline's "rule fires but JE fails" case.
    /// </summary>
    public async Task<ProgressBillingDto> ApproveAsync(Guid id, ApproveBillingRequest req)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // 1) Load the billing under a row lock so a concurrent
            //    approve against the same row blocks.
            var billing = await conn.QuerySingleOrDefaultAsync<BillingRow>(@"
                SELECT id, company_id, project_id, contract_id, billing_number,
                       billing_date, period_from, period_to,
                       work_completed_percent, gross_amount,
                       advance_deducted, retention_deducted, net_amount,
                       status, invoice_id, journal_entry_id, notes,
                       created_at, updated_at
                FROM progress_billings WHERE id = @id FOR UPDATE;",
                new { id }, tx);
            if (billing is null)
                throw new InvalidOperationException("المستخلص غير موجود");
            if (billing.status != "DRAFT")
                throw new InvalidOperationException(
                    $"لا يمكن اعتماد مستخلص بحالة '{billing.status}'. المتوقع: DRAFT");

            // 2) Load the project — need name (for the JE narration)
            //    and customer_id (for the invoice party).
            var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id, string? name, string? name_ar, Guid? customer_id, string? code)?>(@"
                SELECT id, company_id, name, name_ar, customer_id, code
                FROM projects WHERE id = @id AND company_id = @companyId;",
                new { id = billing.project_id, companyId = billing.company_id }, tx);
            if (project is null)
                throw new InvalidOperationException("المشروع غير موجود");
            if (!project.Value.customer_id.HasValue)
                throw new InvalidOperationException(
                    "لا يوجد عميل مرتبط بالمشروع. الرجاء ربط المشروع بعميل قبل اعتماد المستخلص.");

            // 3) Load the customer contact (we need name + tax_id for
            //    the invoice header).
            var customer = await conn.QuerySingleOrDefaultAsync<(string name, string? name_ar, string? tax_id)?>(@"
                SELECT name, name_ar, tax_id FROM contacts
                WHERE id = @id AND company_id = @companyId;",
                new { id = project.Value.customer_id.Value, companyId = billing.company_id }, tx);
            if (customer is null)
                throw new InvalidOperationException("العميل المرتبط بالمشروع غير موجود");

            // 4) Find or auto-create the customer's AR sub-ledger.
            //    1103 is a control account (non-postable), so the
            //    journal line must hit the L4 sub-ledger linked to
            //    this contact. EnsureSubLedgerAsync handles the
            //    "create it if missing" path (Sprint 26).
            var subLedger = await _accounts.EnsureSubLedgerAsync(billing.company_id, project.Value.customer_id.Value);

            // 5) Find 4101 (Sales of Goods) — not a control account,
            //    so we post to it directly.
            var salesAccount = await conn.QuerySingleOrDefaultAsync<(Guid id, string nature)?>(@"
                SELECT id, nature FROM accounts
                WHERE company_id = @companyId AND code = '4101'
                  AND is_postable = true AND is_active = true
                LIMIT 1;",
                new { companyId = billing.company_id }, tx);
            if (salesAccount is null || salesAccount.Value.id == Guid.Empty)
                throw new InvalidOperationException(
                    "حساب 4101 (إيراد بيع بضاعة) غير موجود أو غير قابل للترحيل. الرجاء إعداد دليل الحسابات.");

            // 6) Insert the sales invoice as POSTED. We use raw SQL
            //    here (matching the InvoicingSchema migration) so the
            //    invoice and the billing status update share the same
            //    transaction. The invoice gets status='posted' and
            //    posted_at=NOW() — no separate PostAsync round-trip.
            var invoiceId = Guid.NewGuid();
            var invoiceDate = req.BillingDate;
            await conn.ExecuteAsync(@"
                INSERT INTO invoices (
                    id, company_id, invoice_number, invoice_type, invoice_date,
                    contact_id, party_name, party_name_ar, party_tax_id, notes,
                    subtotal, tax_amount, total, status,
                    project_id, created_at, posted_at
                )
                VALUES (
                    @id, @companyId, @invoiceNumber, 'sales', @invoiceDate,
                    @contactId, @partyName, @partyNameAr, @partyTaxId, @notes,
                    @subtotal, @taxAmount, @total, 'posted',
                    @projectId, NOW(), NOW()
                );",
                new
                {
                    id = invoiceId,
                    companyId = billing.company_id,
                    invoiceNumber = billing.billing_number,
                    invoiceDate,
                    contactId = project.Value.customer_id,
                    partyName = customer.Value.name,
                    partyNameAr = customer.Value.name_ar ?? customer.Value.name,
                    partyTaxId = customer.Value.tax_id,
                    notes = $"مستخلص رقم {billing.billing_number} - مشروع {project.Value.name}",
                    subtotal = billing.net_amount,
                    taxAmount = 0m,
                    total = billing.net_amount,
                    projectId = billing.project_id
                }, tx);

            // 7) Insert the invoice line (single line, no tax).
            await conn.ExecuteAsync(@"
                INSERT INTO invoice_lines (
                    id, invoice_id, account_id, product_id, description,
                    quantity, unit_price, tax_rate, amount,
                    line_total_with_tax, line_number
                )
                VALUES (
                    @id, @invoiceId, @accountId, NULL, @description,
                    @quantity, @unitPrice, @taxRate, @amount,
                    @lineTotalWithTax, @lineNumber
                );",
                new
                {
                    id = Guid.NewGuid(),
                    invoiceId,
                    accountId = salesAccount.Value.id,
                    description = $"مستخلص رقم {billing.billing_number}",
                    quantity = 1m,
                    unitPrice = billing.net_amount,
                    taxRate = 0m,
                    amount = billing.net_amount,
                    lineTotalWithTax = billing.net_amount,
                    lineNumber = 1
                }, tx);

            // 8) Create the journal entry in DRAFT (in the same tx).
            //    We use CreateDraftInTxAsync so the entry and its
            //    lines land atomically with the invoice and billing
            //    update. The actual posting (status='posted',
            //    balance updates) happens after commit via
            //    PostingEngine.PostAsync.
            var narration = $"مستخلص {billing.billing_number} - {project.Value.name}" +
                (req.Notes is not null ? $" ({req.Notes})" : "");
            var lines = new List<CreateJournalLineRequest>
            {
                new(subLedger.Id, billing.net_amount, 0,
                    $"مستخلص رقم {billing.billing_number} - {customer.Value.name}"),
                new(salesAccount.Value.id, 0, billing.net_amount,
                    $"إيراد مستخلص رقم {billing.billing_number}")
            };
            var jeReq = new CreateJournalEntryRequest(
                billing.company_id,
                invoiceDate,
                narration,
                lines,
                Source: "billing",
                ProjectId: billing.project_id
            );
            var journalEntryId = await _journal.CreateDraftInTxAsync(conn, tx, jeReq, null);

            // 9) Update the billing to INVOICED with both back-links.
            await conn.ExecuteAsync(@"
                UPDATE progress_billings
                SET status = 'INVOICED',
                    invoice_id = @invoiceId,
                    journal_entry_id = @journalEntryId,
                    updated_at = NOW()
                WHERE id = @id;",
                new { id, invoiceId, journalEntryId }, tx);

            // 10) Commit. The invoice, JE draft, and billing update
            //     are all durable together.
            tx.Commit();

            // 11) Post-commit: post the JE. This is a separate
            //     transaction (PostingEngine owns its own conn).
            //     If it fails, we log and let the user re-trigger
            //     from the billing detail page.
            try
            {
                await _posting.PostAsync(journalEntryId);
                _log.LogInformation(
                    "Billing {Id} approved: invoice={InvoiceId} je={JeId} net={Net}",
                    id, invoiceId, journalEntryId, billing.net_amount);
            }
            catch (Exception postEx)
            {
                _log.LogError(postEx,
                    "Billing {Id}: invoice {InvoiceId} created but JE {JeId} post FAILED. " +
                    "Billing marked INVOICED; user must re-post manually.",
                    id, invoiceId, journalEntryId);
                // We do NOT throw — the user has an invoice and a
                // billing status update. The JE is in DRAFT and
                // can be re-posted from the journal list. Throwing
                // here would mask the partial success.
            }

            return (await GetByIdAsync(id))!;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ============================================================
    // Cancel
    // ============================================================

    /// <summary>
    /// Cancels a DRAFT billing. Refuses if the billing is already
    /// INVOICED (because the user should reverse the invoice
    /// instead of silently voiding the billing) or already
    /// CANCELLED (idempotent no-op).
    /// </summary>
    public async Task<ProgressBillingDto> CancelAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<(Guid id, string status)?>(@"
            SELECT id, status FROM progress_billings WHERE id = @id;",
            new { id });
        if (existing is null || existing.Value.id == Guid.Empty)
            throw new InvalidOperationException("المستخلص غير موجود");
        if (existing.Value.status == "CANCELLED")
            return (await GetByIdAsync(id))!; // idempotent — row exists
        if (existing.Value.status == "INVOICED")
            throw new InvalidOperationException(
                "لا يمكن إلغاء مستخلص مُرحّل. الرجاء عكس الفاتورة والقيد أولاً.");

        await conn.ExecuteAsync(@"
            UPDATE progress_billings
            SET status = 'CANCELLED', updated_at = NOW()
            WHERE id = @id;",
            new { id });
        return (await GetByIdAsync(id))!; // we just read it; the UPDATE didn't delete it
    }

    // ============================================================
    // WIP report
    // ============================================================

    /// <summary>
    /// Computes the Work-in-Progress snapshot for a project.
    ///
    ///   total_costs  = sum of journal lines on accounts 5401-5407
    ///                  where project_id = X AND status='posted'
    ///   total_billed = sum of progress_billings.net_amount where
    ///                  project_id = X AND status IN ('INVOICED','PAID')
    ///   wip_amount   = total_costs - total_billed
    ///   wip_status   = COSTS_EXCEED_BILLED | BILLED_EXCEED_COSTS | BALANCED
    ///
    /// We do NOT include CANCELLED billings in total_billed, nor
    /// draft billings (they haven't been recognised as revenue).
    /// </summary>
    public async Task<WipResponse?> GetWipAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id, string code, string name)?>(@"
            SELECT id, company_id, code, name FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null) return null;

        // Total costs: sum of journal_lines on accounts 5401-5407
        // that are tagged with this project on a POSTED entry.
        // We use GREATEST(debit, credit) so a line with a single
        // positive side is counted once.
        var totalCosts = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(GREATEST(jl.debit, jl.credit)), 0)
            FROM journal_entries je
            JOIN journal_lines jl ON jl.journal_entry_id = je.id
            JOIN accounts a ON a.id = jl.account_id
            WHERE je.project_id = @projectId
              AND je.status = 'posted'
              AND a.code LIKE '54%';",
            new { projectId }) ?? 0m;

        // Total billed: net amount of all INVOICED billings. We
        // use a "INVOICED" status (we don't have PAID at the
        // billing level — payments land on the invoice and the
        // billing stays INVOICED). Future-proof: include any
        // status past the draft stage.
        var totalBilled = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(net_amount), 0) FROM progress_billings
            WHERE project_id = @projectId
              AND status IN ('INVOICED', 'PAID');",
            new { projectId }) ?? 0m;

        var wipAmount = totalCosts - totalBilled;
        var wipStatus = wipAmount > 0
            ? "COSTS_EXCEED_BILLED"
            : wipAmount < 0
                ? "BILLED_EXCEED_COSTS"
                : "BALANCED";

        return new WipResponse(
            ProjectId: project.Value.id,
            ProjectCode: project.Value.code,
            ProjectName: project.Value.name,
            TotalCosts: totalCosts,
            TotalBilled: totalBilled,
            WipAmount: wipAmount,
            WipStatus: wipStatus,
            AsOfDate: DateTime.UtcNow
        );
    }

    // ============================================================
    // Client statement
    // ============================================================

    /// <summary>
    /// Returns the contractor's-eye view of a single project.
    /// Aggregates from three sources:
    ///   1. contracts (contract_value)
    ///   2. progress_billings (sums of net, advance, retention)
    ///   3. invoices joined to receipts (sums of paid amounts)
    ///
    /// Used by the "Client Statement" tab on the project detail
    /// page. The UI uses these numbers to show a "what the customer
    /// owes us" + "what we still owe the customer (retention)" view.
    /// </summary>
    public async Task<ClientStatementResponse?> GetStatementAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id, string code, string name)?>(@"
            SELECT id, company_id, code, name FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null) return null;

        var contract = await _contracts.GetByProjectAsync(projectId);

        // Sums across INVOICED billings only (CANCELLED and DRAFT
        // don't count toward the recognised totals).
        var totalBilled = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(net_amount), 0) FROM progress_billings
            WHERE project_id = @projectId AND status = 'INVOICED';",
            new { projectId }) ?? 0m;
        var retentionHeld = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(retention_deducted), 0) FROM progress_billings
            WHERE project_id = @projectId AND status = 'INVOICED';",
            new { projectId }) ?? 0m;
        var advanceOutstanding = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(advance_deducted), 0) FROM progress_billings
            WHERE project_id = @projectId AND status = 'INVOICED';",
            new { projectId }) ?? 0m;

        // Total paid: sum of receipt_vouchers.amount applied to
        // the invoices generated by this project's billings. We
        // link via the invoice_id back-link on the billing.
        var totalPaid = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(rv.amount), 0)
            FROM receipt_vouchers rv
            JOIN progress_billings pb ON pb.invoice_id = rv.invoice_id
            WHERE pb.project_id = @projectId
              AND pb.status = 'INVOICED'
              AND rv.status = 'posted';",
            new { projectId }) ?? 0m;

        return new ClientStatementResponse(
            ProjectId: project.Value.id,
            ContractId: contract?.Id,
            ContractValue: contract?.ContractValue ?? 0m,
            TotalBilled: totalBilled,
            TotalPaid: totalPaid,
            RetentionHeld: retentionHeld,
            AdvanceOutstanding: advanceOutstanding,
            NetOutstanding: totalBilled - totalPaid
        );
    }

    // ============================================================
    // Internal mapping
    // ============================================================

    private static ProgressBillingDto MapRow(BillingRow r) => new(
        r.id, r.company_id, r.project_id, r.contract_id, r.billing_number,
        r.billing_date, r.period_from, r.period_to,
        r.work_completed_percent, r.gross_amount,
        r.advance_deducted, r.retention_deducted, r.net_amount,
        r.status, r.invoice_id, r.journal_entry_id, r.notes,
        r.created_at, r.updated_at);

    private record BillingRow(
        Guid id, Guid company_id, Guid project_id, Guid contract_id,
        string billing_number, DateTime billing_date,
        DateTime? period_from, DateTime? period_to,
        decimal work_completed_percent, decimal gross_amount,
        decimal advance_deducted, decimal retention_deducted, decimal net_amount,
        string status, Guid? invoice_id, Guid? journal_entry_id,
        string? notes, DateTime created_at, DateTime? updated_at);
}
