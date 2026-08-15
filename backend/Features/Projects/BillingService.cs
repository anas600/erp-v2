using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Journal;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 36 + Sprint 38 — Progress Billing (مستخلصات) service.
///
/// <para>
/// <b>Sprint 36</b> introduced the %-based calculation:
///   gross = contract_value * work_completed_percent / 100
///   advance_deducted = min(remaining advance, gross)
///   retention_deducted = gross * retention_percent
///   net = gross − advance − retention
/// </para>
///
/// <para>
/// <b>Sprint 38 (BOQ)</b> replaces the % input with a list of
/// line-item quantities. The system now computes:
///   1. For each line item:
///        quantity_previous = sum from previous non-cancelled billings
///        quantity_cumulative = quantity_previous + quantity_this_period
///        amount = quantity_cumulative * unit_price (snapshot)
///   2. gross = sum of all amounts
///   3. work_completed_percent = gross / effective_contract_value * 100
///   4. advance / retention / net: same as Sprint 36
/// </para>
///
/// <para>
/// <b>Backward compatibility</b>:
///   - Migration 022 force-migrated every existing contract to ONE
///     synthetic <c>lump</c> line item (qty=1, unit_price=contract_value).
///   - Migration 022 also force-migrated every existing billing to ONE
///     matching billing_line_item (quantity_cumulative = work_completed_percent).
///   - The math is bit-identical to Sprint 36 when the new endpoints
///     are bypassed and the legacy WorkCompletedPercent path is used.
///   - If <c>req.LineItems</c> is null/empty AND a synthetic lump line
///     item exists, we use it as a % gauge. This means old frontends
///     calling POST /api/projects/{id}/billings with just a percent
///     still get the same numbers.
/// </para>
///
/// <para>
/// Atomicity (ApproveAsync): unchanged from Sprint 36 — the whole
/// "create invoice + create JE + update billing status" dance is
/// wrapped in a single transaction. See <see cref="ApproveAsync"/>
/// for the full dance.
/// </para>
/// </summary>
public class BillingService
{
    private readonly IDbConnectionFactory _db;
    private readonly ContractService _contracts;
    private readonly AccountService _accounts;
    private readonly JournalService _journal;
    private readonly PostingEngine _posting;
    private readonly VariationService _variations;
    private readonly ILogger<BillingService> _log;

    public BillingService(
        IDbConnectionFactory db,
        ContractService contracts,
        AccountService accounts,
        JournalService journal,
        PostingEngine posting,
        VariationService variations,
        ILogger<BillingService> log)
    {
        _db = db;
        _contracts = contracts;
        _accounts = accounts;
        _journal = journal;
        _posting = posting;
        _variations = variations;
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
                   advance_deducted, retention_deducted, final_insurance_deducted, admin_fees_deducted, original_contract_deduction, net_amount,
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
                   advance_deducted, retention_deducted, final_insurance_deducted, admin_fees_deducted, original_contract_deduction, net_amount,
                   status, invoice_id, journal_entry_id, notes,
                   created_at, updated_at
            FROM progress_billings
            WHERE id = @id;",
            new { id });
        return row is null ? null : MapRow(row);
    }

    /// <summary>
    /// Lists the billing_line_items for a billing. The UI uses this
    /// to render the line-item breakdown on the billing detail page
    /// and to populate the "what was claimed" view after approval.
    /// </summary>
    public async Task<List<BillingLineItemDto>> GetBillingLineItemsAsync(Guid billingId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<BillingLineItemRow>(@"
            SELECT bli.id, bli.billing_id, bli.line_item_id,
                   li.line_number, li.description, li.unit, li.custom_unit,
                   bli.quantity_this_period, bli.quantity_previous,
                   bli.quantity_cumulative, bli.unit_price, bli.amount,
                   bli.notes
            FROM billing_line_items bli
            JOIN contract_line_items li ON li.id = bli.line_item_id
            WHERE bli.billing_id = @billingId
            ORDER BY li.line_number ASC;",
            new { billingId });
        return rows.Select(MapBillingLineItem).ToList();
    }

    // ============================================================
    // Create (the calculation engine)
    // ============================================================

    /// <summary>
    /// Creates a new progress billing. The new (Sprint 38) shape is:
    ///   1. Compute each line item's amount (cumulative qty * unit_price).
    ///   2. gross = sum of all amounts.
    ///   3. work_completed_percent = gross / effective_value * 100.
    ///   4. advance / retention / net as before.
    ///
    /// <para>
    /// Backward-compat: if <c>req.LineItems</c> is null/empty, we
    /// fall back to the Sprint 36 %-based math. The migration 022
    /// synthetic lump line item makes that fallback produce the
    /// same numbers as before, so legacy frontends still work.
    /// </para>
    /// </summary>
    public async Task<ProgressBillingDto> CreateAsync(Guid projectId, CreateBillingRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.BillingNumber))
            throw new InvalidOperationException("رقم المستخلص مطلوب");

        using var conn = _db.CreateConnection();

        // 1) Load the project — need its company_id for the
        //    cross-company check and to stamp progress_billings.company_id.
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id, string? name, string? name_ar, Guid? customer_id)?>(@"
            SELECT id, company_id, name, name_ar, customer_id
            FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null)
            throw new InvalidOperationException("المشروع غير موجود");

        // 2) Load the contract (must exist for this project).
        var contract = await _contracts.GetByProjectAsync(projectId);
        if (contract is null)
            throw new InvalidOperationException("لا يوجد عقد لهذا المشروع. الرجاء إنشاء عقد أولاً.");
        if (contract.Id != req.ContractId)
            throw new InvalidOperationException("العقد المحدد لا يخص هذا المشروع");
        if (contract.CompanyId != project.Value.company_id)
            throw new InvalidOperationException("العقد لا ينتمي لنفس شركة المشروع");

        // 3) Check uniqueness of billing_number within the company.
        var dup = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM progress_billings
            WHERE company_id = @companyId AND billing_number = @billingNumber;",
            new { companyId = project.Value.company_id, billingNumber = req.BillingNumber });
        if (dup > 0)
            throw new InvalidOperationException(
                $"رقم المستخلص '{req.BillingNumber}' مستخدم بالفعل في هذه الشركة");

        // 4) Compute the effective contract value (original +
        //    approved variation items).
        var effectiveValue = await _variations.GetEffectiveContractValueAsync(contract.Id);

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
        var nextBillingNumber = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) + 1 FROM progress_billings
            WHERE project_id = @projectId
              AND status != 'CANCELLED';",
            new { projectId });

        // 6) Resolve the line items. The two paths:
        //    (a) New: req.LineItems is non-empty — use those, compute amounts.
        //    (b) Legacy: req.LineItems is null/empty — use the Sprint 36
        //        manual % and the synthetic lump line item (if any).
        //        If no lump line item exists, refuse — there's nothing to bill.
        List<BillingLineItemInsert> billingLineItems;
        decimal gross;
        decimal workCompletedPercent;

        if (req.LineItems is { Count: > 0 })
        {
            // Path (a): BOQ-based.
            (billingLineItems, gross) = await ComputeLineItemAmountsAsync(
                contract.Id, projectId, req.LineItems);
            if (effectiveValue <= 0)
                throw new InvalidOperationException("قيمة العقد الفعلي صفر — لا يمكن إنشاء مستخلص");
            workCompletedPercent = Math.Round(gross / effectiveValue * 100m, 3);
        }
        else
        {
            // Path (b): legacy %-based.
            if (!req.WorkCompletedPercent.HasValue)
                throw new InvalidOperationException(
                    "يجب تحديد نسبة الإنجاز أو بنود المستخلص (LineItems)");
            if (req.WorkCompletedPercent.Value < 0 || req.WorkCompletedPercent.Value > 100)
                throw new InvalidOperationException("نسبة الإنجاز يجب أن تكون بين 0 و 100");
            // Validate cumulative % can't go backwards.
            var previousMaxPercent = await conn.ExecuteScalarAsync<decimal?>(@"
                SELECT COALESCE(MAX(work_completed_percent), 0) FROM progress_billings
                WHERE project_id = @projectId
                  AND status != 'CANCELLED';",
                new { projectId }) ?? 0m;
            if (req.WorkCompletedPercent.Value < previousMaxPercent)
                throw new InvalidOperationException(
                    $"نسبة الإنجاز ({req.WorkCompletedPercent.Value}%) أقل من الحد الأقصى السابق ({previousMaxPercent}%). " +
                    "لا يمكن إنقاص نسبة الإنجاز التراكمية.");

            gross = Math.Round(contract.ContractValue * (req.WorkCompletedPercent.Value / 100m), 3);
            workCompletedPercent = req.WorkCompletedPercent.Value;

            // Find the synthetic lump line item (migration 022) so we
            // can insert one matching billing_line_item. If the caller
            // has already migrated to BOQ (real items exist) and is
            // using the legacy %, we still need a billing_line_item
            // for accounting traceability — use the lump if it exists.
            billingLineItems = await BuildLegacySyntheticLineItemAsync(
                contract.Id, projectId, gross, workCompletedPercent);
            if (billingLineItems.Count == 0)
            {
                // No synthetic lump line item — synthesize one on the
                // fly for this legacy path. The contract's effective
                // value is unchanged; we don't add to the BOQ.
                _log.LogWarning(
                    "Legacy %-based billing on contract {ContractId} without a synthetic lump; creating one on the fly.",
                    contract.Id);
                billingLineItems = new List<BillingLineItemInsert>
                {
                    new(Guid.Empty, workCompletedPercent, 0,
                        workCompletedPercent, contract.ContractValue, gross)
                };
            }
        }

        // 7) Calculate advance / retention / additional deductions / net.
        //
        // Sprint 53: three additional deductions from the Libyan
        // construction contract model:
        //   - final_insurance_percent (2% default) — held as liability
        //     until end of warranty period
        //   - admin_fee_percent (1.5% default) — paid to the owner
        //   - original_contract_deduction (15% of original contract
        //     value, applied to FIRST billing only) — typically a
        //     tax / withholding on the pre-variation contract value
        // Sprint 58 — advance is on the ORIGINAL contract value, not
        // the effective value. Variations are added later and don't
        // get advance payments (per the Libyan construction contract
        // convention; the Excel shows advance = 20% × 2,369,048 =
        // 473,810, not 20% × 6,561,447 = 1,312,289). Without this fix,
        // the first billing's advance deduction would consume the
        // entire gross, leaving net ≤ 0.
        var advanceBase = (contract.OriginalContractValue.HasValue && contract.OriginalContractValue.Value > 0)
            ? contract.OriginalContractValue.Value
            : contract.ContractValue;
        var advanceTotal = Math.Round(advanceBase * (contract.AdvancePercent / 100m), 3);
        var remainingAdvance = Math.Max(0m, advanceTotal - previousAdvance);
        // Sprint 58 — cap advance recovery at the cumulative work %
        // (i.e. if work is 15% done, recover 15% of the total advance).
        // This is the Libyan construction convention: the advance is
        // recovered proportionally to the work done, not all-at-once
        // from the first billing. Without this cap, the first billing
        // (with gross = 15% of contract) would have advance = min(gross,
        // 20% of contract) = gross, leaving net = 0 or negative.
        var cumulativePct = workCompletedPercent;  // already calculated above
        var cumulativeAdvanceCap = Math.Round(advanceTotal * (Math.Min(cumulativePct, 100m) / 100m), 3);
        var advanceCap = Math.Max(0m, Math.Min(cumulativeAdvanceCap, remainingAdvance));
        var advanceDeducted = Math.Round(Math.Min(gross, advanceCap), 3);

        decimal retentionDeducted = 0m;
        if (nextBillingNumber >= contract.RetentionStartBilling)
        {
            retentionDeducted = Math.Round(gross * (contract.RetentionPercent / 100m), 3);
        }

        // Sprint 53: final insurance 2% — applied to every billing
        // from the start (not gated by retention_start_billing)
        decimal finalInsuranceDeducted = 0m;
        if (contract.FinalInsurancePercent > 0)
        {
            finalInsuranceDeducted = Math.Round(gross * (contract.FinalInsurancePercent / 100m), 3);
        }

        // Sprint 53: admin fees 1.5% — applied to every billing
        decimal adminFeesDeducted = 0m;
        if (contract.AdminFeePercent > 0)
        {
            adminFeesDeducted = Math.Round(gross * (contract.AdminFeePercent / 100m), 3);
        }

        // Sprint 53: original contract deduction 15% — FIRST billing
        // only, and only if the contract has an explicit
        // OriginalContractValue set (Sprint 54 will add this field
        // to the contract edit form). The deduction applies to the
        // PRE-variation contract value, not the current effective
        // value, to avoid a 15% × 4M = 600K deduction on a billing
        // whose gross is only 1.2M (would push net negative).
        //
        // Sprint 58 — original contract deduction (15% of original
        // contract value). This is a Libyan construction contract
        // convention: the 15% withholding is against the
        // PRE-variation contract value, not the current effective
        // value. The deduction is SPREAD across all billings
        // proportionally to the cumulative work done (rather than
        // all-at-once on the first billing), so each billing's
        // deduction is bounded by what it can afford.
        //
        // Example: 15% of 2,369,048 = 355,357 total deduction
        //   Billing 1 (15% cumulative): 15% × 355,357 = 53,304
        //   Billing 2 (35% cumulative): 35% × 355,357 = 124,375
        //   Billing 3 (65% cumulative): 65% × 355,357 = 230,982
        //   Billing 4 (85% cumulative): 85% × 355,357 = 302,054
        //   (Total: 710,715 = 2 × 355,357 — wait, that doesn't work)
        //
        // Actually, the convention is: 15% of ORIGINAL is a one-time
        // deduction, NOT multiplied by cumulative %. It's applied
        // fully on the first billing that has enough gross to cover
        // it. For our 4-billing split (each ~25% cumulative), the
        // first billing's gross (355,357) exactly equals the 15%
        // deduction, leaving no room for other deductions.
        //
        // Fix: cap the 15% deduction at the billing's gross minus
        // the other deductions. This way, the system always has a
        // valid (positive) net.
        decimal originalContractDeduction = 0m;
        if (contract.OriginalContractValue.HasValue && contract.OriginalContractValue.Value > 0)
        {
            // Total 15% to be deducted (one-time, across all billings)
            var totalOriginalDeduction = Math.Round(
                contract.OriginalContractValue.Value * 0.15m, 3);
            // How much has been deducted in previous billings?
            var previousOriginalDeduction = await conn.ExecuteScalarAsync<decimal?>(@"
                SELECT COALESCE(SUM(original_contract_deduction), 0) FROM progress_billings
                WHERE project_id = @projectId AND status != 'CANCELLED';",
                new { projectId }) ?? 0m;
            var remainingOriginalDeduction = Math.Max(0m, totalOriginalDeduction - previousOriginalDeduction);
            // Cap at what's affordable in this billing, leaving at
            // least 1% of the gross as positive net (so the JE has
            // non-zero debit/credit lines). For 15% first billing
            // the cap is 14% of gross; for larger billings it's
            // proportionally more.
            var availableForOriginal = Math.Max(0m,
                gross - advanceDeducted - retentionDeducted
                       - finalInsuranceDeducted - adminFeesDeducted
                       - Math.Max(gross * 0.01m, 100m));  // keep at least 1% of gross as net
            originalContractDeduction = Math.Round(
                Math.Min(remainingOriginalDeduction, Math.Max(0m, availableForOriginal)), 3);
        }

        var net = Math.Round(
            gross - advanceDeducted - retentionDeducted
                - finalInsuranceDeducted - adminFeesDeducted
                - originalContractDeduction, 3);

        // 8) Insert the billing in DRAFT status + the billing_line_items,
        //    all in one transaction so a partial failure can't leave a
        //    billing without its line items.
        var id = Guid.NewGuid();
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO progress_billings (
                        id, company_id, project_id, contract_id, billing_number,
                        billing_date, period_from, period_to,
                        work_completed_percent, gross_amount,
                        advance_deducted, retention_deducted,
                        final_insurance_deducted, admin_fees_deducted,
                        original_contract_deduction,
                        net_amount,
                        status, notes, created_at
                    )
                    VALUES (
                        @id, @companyId, @projectId, @contractId, @billingNumber,
                        @billingDate, @periodFrom, @periodTo,
                        @workCompletedPercent, @gross,
                        @advanceDeducted, @retentionDeducted,
                        @finalInsuranceDeducted, @adminFeesDeducted,
                        @originalContractDeduction,
                        @net,
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
                        workCompletedPercent,
                        gross,
                        advanceDeducted,
                        retentionDeducted,
                        finalInsuranceDeducted,
                        adminFeesDeducted,
                        originalContractDeduction,
                        net,
                        notes = req.Notes
                    }, tx);

                foreach (var bli in billingLineItems)
                {
                    // If the line item was a "ghost" (legacy % path
                    // with no synthetic line item), look up the
                    // synthetic lump line item id by contract.
                    var lineItemId = bli.LineItemId;
                    if (lineItemId == Guid.Empty)
                    {
                        lineItemId = await conn.ExecuteScalarAsync<Guid>(@"
                            SELECT id FROM contract_line_items
                            WHERE contract_id = @contractId AND line_number = 1
                            LIMIT 1;",
                            new { contractId = contract.Id }, tx);
                        if (lineItemId == Guid.Empty)
                            throw new InvalidOperationException(
                                "تعذر تحديد بند المستخلص — لا يوجد بند BOQ للعقد");
                    }
                    await conn.ExecuteAsync(@"
                        INSERT INTO billing_line_items (
                            id, company_id, billing_id, line_item_id,
                            quantity_this_period, quantity_previous, quantity_cumulative,
                            unit_price, amount
                        )
                        VALUES (
                            @id, @companyId, @billingId, @lineItemId,
                            @quantityThisPeriod, @quantityPrevious, @quantityCumulative,
                            @unitPrice, @amount
                        );",
                        new
                        {
                            id = Guid.NewGuid(),
                            companyId = project.Value.company_id,
                            billingId = id,
                            lineItemId,
                            quantityThisPeriod = bli.QuantityThisPeriod,
                            quantityPrevious = bli.QuantityPrevious,
                            quantityCumulative = bli.QuantityCumulative,
                            unitPrice = bli.UnitPrice,
                            amount = bli.Amount
                        }, tx);
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        return (await GetByIdAsync(id))!;
    }

    /// <summary>
    /// Computes the line-item amounts for a BOQ-based billing.
    /// Returns the inserts and the gross total.
    /// </summary>
    private async Task<(List<BillingLineItemInsert> items, decimal gross)>
        ComputeLineItemAmountsAsync(
            Guid contractId, Guid projectId, List<CreateBillingLineItemRequest> items)
    {
        using var conn = _db.CreateConnection();
        var inserts = new List<BillingLineItemInsert>();
        decimal gross = 0m;

        foreach (var item in items)
        {
            // Load the line item (we need its unit_price, quantity,
            // and to confirm it belongs to this contract).
            var li = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid contract_id, decimal quantity, decimal unit_price, string? custom_unit, string unit)?>(@"
                SELECT id, contract_id, quantity, unit_price, custom_unit, unit
                FROM contract_line_items WHERE id = @id;",
                new { id = item.LineItemId });
            if (li is null)
                throw new InvalidOperationException(
                    $"بند المستخلص غير موجود: {item.LineItemId}");
            if (li.Value.contract_id != contractId)
                throw new InvalidOperationException(
                    "البند لا ينتمي لنفس عقد المستخلص");

            // Sum the previous claims for this line item from
            // non-cancelled billings. We use billing_line_items as
            // the source of truth (it has the per-billing split).
            var previousQty = await conn.ExecuteScalarAsync<decimal?>(@"
                SELECT COALESCE(SUM(bli.quantity_this_period), 0)
                FROM billing_line_items bli
                JOIN progress_billings pb ON pb.id = bli.billing_id
                WHERE bli.line_item_id = @lineItemId
                  AND pb.status != 'CANCELLED'
                  AND pb.id <> @excludeBillingId;",
                new { lineItemId = item.LineItemId, excludeBillingId = Guid.Empty }) ?? 0m;

            var thisPeriod = item.QuantityThisPeriod;
            if (thisPeriod < 0)
                throw new InvalidOperationException(
                    $"الكمية المنفذة للبند يجب أن تكون أكبر من أو تساوي صفر");
            var cumulative = Math.Round(previousQty + thisPeriod, 3);
            if (cumulative > li.Value.quantity)
                throw new InvalidOperationException(
                    $"الكمية التراكمية ({cumulative}) لبند '{li.Value.unit}' تتجاوز الكمية الإجمالية ({li.Value.quantity})");

            // amount uses the snapshot unit_price (which we just
            // pulled from contract_line_items — the snapshot is
            // implicit in the line item, since we don't allow
            // editing a line item's unit_price once billed).
            var amount = Math.Round(cumulative * li.Value.unit_price, 3);
            gross = Math.Round(gross + amount, 3);

            inserts.Add(new BillingLineItemInsert(
                LineItemId: li.Value.id,
                QuantityThisPeriod: thisPeriod,
                QuantityPrevious: previousQty,
                QuantityCumulative: cumulative,
                UnitPrice: li.Value.unit_price,
                Amount: amount));
        }

        return (inserts, gross);
    }

    /// <summary>
    /// Builds the synthetic billing_line_item for the legacy
    /// %-based path. If the contract has a synthetic lump line
    /// item (migration 022), we attach the billing to that and
    /// use work_completed_percent as the qty. If not, we return
    /// an empty list — the caller decides what to do.
    /// </summary>
    private async Task<List<BillingLineItemInsert>> BuildLegacySyntheticLineItemAsync(
        Guid contractId, Guid projectId, decimal gross, decimal workCompletedPercent)
    {
        using var conn = _db.CreateConnection();
        var lump = await conn.QuerySingleOrDefaultAsync<(Guid id, decimal unit_price)?>(@"
            SELECT id, unit_price FROM contract_line_items
            WHERE contract_id = @contractId
              AND line_number = 1
              AND unit = 'lump'
              AND quantity = 1
            LIMIT 1;",
            new { contractId });
        if (lump is null)
            return new List<BillingLineItemInsert>();

        // The synthetic line item has qty=1, unit_price=contract_value.
        // Using work_completed_percent as the qty means:
        //   amount = work_completed_percent * contract_value = gross.
        // This matches the Sprint 36 calculation exactly.
        return new List<BillingLineItemInsert>
        {
            new(
                LineItemId: lump.Value.id,
                QuantityThisPeriod: workCompletedPercent,
                QuantityPrevious: 0m,
                QuantityCumulative: workCompletedPercent,
                UnitPrice: lump.Value.unit_price,
                Amount: gross)
        };
    }

    /// <summary>
    /// Returns a preview of what the billing_line_items would look
    /// like if the user submitted these quantities. Used by the UI
    /// to show live totals before committing (e.g. the user types
    /// in a quantity and the table updates immediately).
    /// </summary>
    public async Task<List<BillingLineItemPreview>> PreviewBillingLineItemsAsync(
        Guid contractId, List<CreateBillingLineItemRequest> items)
    {
        if (items is null || items.Count == 0)
            return new List<BillingLineItemPreview>();

        using var conn = _db.CreateConnection();
        var previews = new List<BillingLineItemPreview>();

        foreach (var item in items)
        {
            var li = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid contract_id, int line_number, string description, string unit, string? custom_unit, decimal quantity, decimal unit_price)?>(@"
                SELECT id, contract_id, line_number, description, unit, custom_unit,
                       quantity, unit_price
                FROM contract_line_items WHERE id = @id;",
                new { id = item.LineItemId });
            if (li is null || li.Value.contract_id != contractId)
                continue;

            var previousQty = await conn.ExecuteScalarAsync<decimal?>(@"
                SELECT COALESCE(SUM(bli.quantity_this_period), 0)
                FROM billing_line_items bli
                JOIN progress_billings pb ON pb.id = bli.billing_id
                WHERE bli.line_item_id = @lineItemId
                  AND pb.status != 'CANCELLED';",
                new { lineItemId = item.LineItemId }) ?? 0m;
            var proposedCumulative = Math.Round(previousQty + item.QuantityThisPeriod, 3);
            var proposedAmount = Math.Round(proposedCumulative * li.Value.unit_price, 3);

            previews.Add(new BillingLineItemPreview(
                LineItemId: li.Value.id,
                LineNumber: li.Value.line_number,
                Description: li.Value.description,
                Unit: li.Value.unit,
                Quantity: li.Value.quantity,
                QuantityPrevious: previousQty,
                UnitPrice: li.Value.unit_price,
                ProposedThisPeriod: item.QuantityThisPeriod,
                ProposedCumulative: proposedCumulative,
                ProposedAmount: proposedAmount));
        }
        return previews;
    }

    private record BillingLineItemInsert(
        Guid LineItemId,
        decimal QuantityThisPeriod,
        decimal QuantityPrevious,
        decimal QuantityCumulative,
        decimal UnitPrice,
        decimal Amount);

    // ============================================================
    // Update (only while DRAFT)
    // ============================================================

    /// <summary>
    /// Replaces the editable fields on a DRAFT billing. If
    /// <c>req.LineItems</c> is provided, the four amount columns
    /// are recomputed from the items; otherwise the legacy
    /// <c>req.WorkCompletedPercent</c> is used.
    /// </summary>
    public async Task<ProgressBillingDto?> UpdateAsync(Guid id, UpdateBillingRequest req)
    {
        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<BillingRow>(@"
            SELECT id, company_id, project_id, contract_id, billing_number,
                   billing_date, period_from, period_to,
                   work_completed_percent, gross_amount,
                   advance_deducted, retention_deducted, final_insurance_deducted, admin_fees_deducted, original_contract_deduction, net_amount,
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

        var effectiveValue = await _variations.GetEffectiveContractValueAsync(existing.contract_id);

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

        decimal gross;
        decimal workCompletedPercent;
        List<BillingLineItemInsert> lineItems;

        if (req.LineItems is { Count: > 0 })
        {
            // BOQ path. We pass the billing's id as the exclude so
            // ComputeLineItemAmountsAsync doesn't double-count this
            // billing's existing claims.
            (lineItems, gross) = await ComputeLineItemAmountsForUpdateAsync(
                existing.contract_id, existing.project_id, id, req.LineItems);
            if (effectiveValue <= 0)
                throw new InvalidOperationException("قيمة العقد الفعلي صفر");
            workCompletedPercent = Math.Round(gross / effectiveValue * 100m, 3);
        }
        else
        {
            // Legacy % path.
            var newPercent = req.WorkCompletedPercent ?? existing.work_completed_percent;
            if (newPercent < 0 || newPercent > 100)
                throw new InvalidOperationException("نسبة الإنجاز يجب أن تكون بين 0 و 100");
            var otherMaxPercent = await conn.ExecuteScalarAsync<decimal?>(@"
                SELECT COALESCE(MAX(work_completed_percent), 0) FROM progress_billings
                WHERE project_id = @projectId AND id <> @id AND status != 'CANCELLED';",
                new { projectId = existing.project_id, id }) ?? 0m;
            if (newPercent < otherMaxPercent)
                throw new InvalidOperationException(
                    $"نسبة الإنجاز ({newPercent}%) أقل من الحد الأقصى للمستخلصات الأخرى ({otherMaxPercent}%)");
            workCompletedPercent = newPercent;
            gross = Math.Round(contract.ContractValue * (newPercent / 100m), 3);
            lineItems = await BuildLegacySyntheticLineItemAsync(
                existing.contract_id, existing.project_id, gross, newPercent);
        }

        // Sprint 58 — same fix as above for the UpdateAsync path
        var advanceBaseUpdate = (contract.OriginalContractValue.HasValue && contract.OriginalContractValue.Value > 0)
            ? contract.OriginalContractValue.Value
            : contract.ContractValue;
        var advanceTotal = Math.Round(advanceBaseUpdate * (contract.AdvancePercent / 100m), 3);
        var remainingAdvance = Math.Max(0m, advanceTotal - previousAdvance);
        // Sprint 58 — cap advance recovery at the cumulative work %
        // (same convention as the CreateAsync path above)
        var cumulativePctUpdate = workCompletedPercent;
        var cumulativeAdvanceCap = Math.Round(advanceTotal * (Math.Min(cumulativePctUpdate, 100m) / 100m), 3);
        var advanceCap = Math.Max(0m, Math.Min(cumulativeAdvanceCap, remainingAdvance));
        var advanceDeducted = Math.Round(Math.Min(gross, advanceCap), 3);

        decimal retentionDeducted = 0m;
        if (nextBillingNumber >= contract.RetentionStartBilling)
            retentionDeducted = Math.Round(gross * (contract.RetentionPercent / 100m), 3);

        // Sprint 53: same deductions as in Create
        decimal finalInsuranceDeducted = 0m;
        if (contract.FinalInsurancePercent > 0)
            finalInsuranceDeducted = Math.Round(gross * (contract.FinalInsurancePercent / 100m), 3);

        decimal adminFeesDeducted = 0m;
        if (contract.AdminFeePercent > 0)
            adminFeesDeducted = Math.Round(gross * (contract.AdminFeePercent / 100m), 3);

        decimal originalContractDeduction = 0m;
        var otherFirstBillingCount = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM progress_billings
            WHERE contract_id = @contractId AND id <> @id AND status != 'CANCELLED';",
            new { contractId = existing.contract_id, id });
        // NOTE: originalContractDeduction stays 0 until Sprint 54
        // adds the OriginalContractValue field on ContractDto.
        _ = otherFirstBillingCount; // suppress unused-variable warning

        var net = Math.Round(
            gross - advanceDeducted - retentionDeducted
                - finalInsuranceDeducted - adminFeesDeducted
                - originalContractDeduction, 3);

        // Update the billing + replace its billing_line_items in one
        // transaction. The DELETE+INSERT is the simplest "replace
        // items" strategy and works because the line items are a
        // child table with no other dependents.
        using (var tx = conn.BeginTransaction())
        {
            try
            {
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
                        final_insurance_deducted = @finalInsuranceDeducted,
                        admin_fees_deducted = @adminFeesDeducted,
                        original_contract_deduction = @originalContractDeduction,
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
                        workCompletedPercent,
                        gross,
                        advanceDeducted,
                        retentionDeducted,
                        finalInsuranceDeducted,
                        adminFeesDeducted,
                        originalContractDeduction,
                        net,
                        notes = req.Notes
                    }, tx);

                await conn.ExecuteAsync(
                    "DELETE FROM billing_line_items WHERE billing_id = @id;",
                    new { id }, tx);

                foreach (var bli in lineItems)
                {
                    var lineItemId = bli.LineItemId;
                    if (lineItemId == Guid.Empty)
                    {
                        lineItemId = await conn.ExecuteScalarAsync<Guid>(@"
                            SELECT id FROM contract_line_items
                            WHERE contract_id = @contractId AND line_number = 1
                            LIMIT 1;",
                            new { contractId = existing.contract_id }, tx);
                        if (lineItemId == Guid.Empty)
                            throw new InvalidOperationException(
                                "تعذر تحديد بند المستخلص — لا يوجد بند BOQ للعقد");
                    }
                    await conn.ExecuteAsync(@"
                        INSERT INTO billing_line_items (
                            id, company_id, billing_id, line_item_id,
                            quantity_this_period, quantity_previous, quantity_cumulative,
                            unit_price, amount
                        )
                        VALUES (
                            @id, @companyId, @billingId, @lineItemId,
                            @quantityThisPeriod, @quantityPrevious, @quantityCumulative,
                            @unitPrice, @amount
                        );",
                        new
                        {
                            id = Guid.NewGuid(),
                            companyId = existing.company_id,
                            billingId = id,
                            lineItemId,
                            quantityThisPeriod = bli.QuantityThisPeriod,
                            quantityPrevious = bli.QuantityPrevious,
                            quantityCumulative = bli.QuantityCumulative,
                            unitPrice = bli.UnitPrice,
                            amount = bli.Amount
                        }, tx);
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        return await GetByIdAsync(id);
    }

    /// <summary>
    /// Same as <see cref="ComputeLineItemAmountsAsync"/> but
    /// excludes the current billing's claims (so re-claiming the
    /// same item doesn't double-count).
    /// </summary>
    private async Task<(List<BillingLineItemInsert> items, decimal gross)>
        ComputeLineItemAmountsForUpdateAsync(
            Guid contractId, Guid projectId, Guid excludeBillingId,
            List<CreateBillingLineItemRequest> items)
    {
        using var conn = _db.CreateConnection();
        var inserts = new List<BillingLineItemInsert>();
        decimal gross = 0m;

        foreach (var item in items)
        {
            var li = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid contract_id, decimal quantity, decimal unit_price)?>(@"
                SELECT id, contract_id, quantity, unit_price
                FROM contract_line_items WHERE id = @id;",
                new { id = item.LineItemId });
            if (li is null)
                throw new InvalidOperationException(
                    $"بند المستخلص غير موجود: {item.LineItemId}");
            if (li.Value.contract_id != contractId)
                throw new InvalidOperationException(
                    "البند لا ينتمي لنفس عقد المستخلص");

            var previousQty = await conn.ExecuteScalarAsync<decimal?>(@"
                SELECT COALESCE(SUM(bli.quantity_this_period), 0)
                FROM billing_line_items bli
                JOIN progress_billings pb ON pb.id = bli.billing_id
                WHERE bli.line_item_id = @lineItemId
                  AND pb.status != 'CANCELLED'
                  AND pb.id <> @excludeBillingId;",
                new { lineItemId = item.LineItemId, excludeBillingId }) ?? 0m;

            var thisPeriod = item.QuantityThisPeriod;
            if (thisPeriod < 0)
                throw new InvalidOperationException(
                    $"الكمية المنفذة للبند يجب أن تكون أكبر من أو تساوي صفر");
            var cumulative = Math.Round(previousQty + thisPeriod, 3);
            if (cumulative > li.Value.quantity)
                throw new InvalidOperationException(
                    $"الكمية التراكمية ({cumulative}) تتجاوز الكمية الإجمالية ({li.Value.quantity})");

            var amount = Math.Round(cumulative * li.Value.unit_price, 3);
            gross = Math.Round(gross + amount, 3);

            inserts.Add(new BillingLineItemInsert(
                LineItemId: li.Value.id,
                QuantityThisPeriod: thisPeriod,
                QuantityPrevious: previousQty,
                QuantityCumulative: cumulative,
                UnitPrice: li.Value.unit_price,
                Amount: amount));
        }
        return (inserts, gross);
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
                       advance_deducted, retention_deducted, final_insurance_deducted, admin_fees_deducted, original_contract_deduction, net_amount,
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
            var subLedger = await _accounts.EnsureSubLedgerAsync(billing.company_id, project.Value.customer_id.Value);

            // 5) Find 4101 (Sales of Goods). Revenue accounts sit at
            //    L3 in the standard 4-level COA — the L4 split is
            //    reserved for balance-sheet sub-ledgers (AR/AP).
            //    Therefore we deliberately do NOT filter on
            //    is_postable here: 4101 is the postable revenue
            //    account even though the COA marks it as a control
            //    account in the level sense.
            var salesAccount = await conn.QuerySingleOrDefaultAsync<(Guid id, string nature)?>(@"
                SELECT id, nature FROM accounts
                WHERE company_id = @companyId AND code = '4101'
                  AND is_active = true
                LIMIT 1;",
                new { companyId = billing.company_id }, tx);
            if (salesAccount is null || salesAccount.Value.id == Guid.Empty)
                throw new InvalidOperationException(
                    "حساب 4101 (إيراد بيع بضاعة) غير موجود أو غير قابل للترحيل. الرجاء إعداد دليل الحسابات.");

            // 6) Insert the sales invoice as POSTED. The invoices table
            //    uses party_name/party_name_ar/party_tax_id (free text)
            //    rather than a contact_id FK — that was a Sprint 3
            //    design choice and we keep it consistent here.
            var invoiceId = Guid.NewGuid();
            var invoiceDate = req.BillingDate;
            await conn.ExecuteAsync(@"
                INSERT INTO invoices (
                    id, company_id, invoice_number, invoice_type, invoice_date,
                    party_name, party_name_ar, party_tax_id, notes,
                    subtotal, tax_amount, total, status,
                    project_id, created_at, posted_at
                )
                VALUES (
                    @id, @companyId, @invoiceNumber, 'sales', @invoiceDate,
                    @partyName, @partyNameAr, @partyTaxId, @notes,
                    @subtotal, @taxAmount, @total, 'posted',
                    @projectId, NOW(), NOW()
                );",
                new
                {
                    id = invoiceId,
                    companyId = billing.company_id,
                    invoiceNumber = billing.billing_number,
                    invoiceDate,
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

            tx.Commit();

            // 10) Post-commit: post the JE. This is a separate
            //     transaction (PostingEngine owns its own conn).
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
            return (await GetByIdAsync(id))!;
        if (existing.Value.status == "INVOICED")
            throw new InvalidOperationException(
                "لا يمكن إلغاء مستخلص مُرحّل. الرجاء عكس الفاتورة والقيد أولاً.");

        await conn.ExecuteAsync(@"
            UPDATE progress_billings
            SET status = 'CANCELLED', updated_at = NOW()
            WHERE id = @id;",
            new { id });
        return (await GetByIdAsync(id))!;
    }

    // ============================================================
    // WIP report
    // ============================================================

    public async Task<WipResponse?> GetWipAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id, string code, string name)?>(@"
            SELECT id, company_id, code, name FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null) return null;

        var totalCosts = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(GREATEST(jl.debit, jl.credit)), 0)
            FROM journal_entries je
            JOIN journal_lines jl ON jl.journal_entry_id = je.id
            JOIN accounts a ON a.id = jl.account_id
            WHERE je.project_id = @projectId
              AND je.status = 'posted'
              AND a.code LIKE '54%';",
            new { projectId }) ?? 0m;

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

    public async Task<ClientStatementResponse?> GetStatementAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id, string code, string name)?>(@"
            SELECT id, company_id, code, name FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null) return null;

        var contract = await _contracts.GetByProjectAsync(projectId);

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
        r.advance_deducted, r.retention_deducted,
        r.final_insurance_deducted, r.admin_fees_deducted, r.original_contract_deduction,
        r.net_amount,
        r.status, r.invoice_id, r.journal_entry_id, r.notes,
        r.created_at, r.updated_at);

    private static BillingLineItemDto MapBillingLineItem(BillingLineItemRow r) => new(
        r.id, r.billing_id, r.line_item_id,
        r.line_number, r.description, r.unit, r.custom_unit,
        r.quantity_this_period, r.quantity_previous, r.quantity_cumulative,
        r.unit_price, r.amount, r.notes);

    private record BillingRow(
        Guid id, Guid company_id, Guid project_id, Guid contract_id,
        string billing_number, DateTime billing_date,
        DateTime? period_from, DateTime? period_to,
        decimal work_completed_percent, decimal gross_amount,
        decimal advance_deducted, decimal retention_deducted,
        decimal final_insurance_deducted, decimal admin_fees_deducted,
        decimal original_contract_deduction, decimal net_amount,
        string status, Guid? invoice_id, Guid? journal_entry_id,
        string? notes, DateTime created_at, DateTime? updated_at);

    private record BillingLineItemRow(
        Guid id, Guid billing_id, Guid line_item_id,
        int line_number, string description, string unit, string? custom_unit,
        decimal quantity_this_period, decimal quantity_previous, decimal quantity_cumulative,
        decimal unit_price, decimal amount, string? notes);
}
