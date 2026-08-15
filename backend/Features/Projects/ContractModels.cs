namespace ErpV2.Features.Projects;

// ============================================================
// Sprint 36 — Contracts (العقود)
// ============================================================
//
// A contract captures the negotiated terms between the project
// owner (us) and the customer. It is the anchor for every
// progress billing (مستخلص):
//   - contract_value         → determines gross_amount per billing
//   - advance_percent        → how much of the contract is collected
//                              up-front and deducted from the early
//                              billings
//   - retention_percent      → how much of each billing is held back
//                              as a guarantee, released on completion
//   - retention_start_billing→ which billing number starts retaining
//                              (1 = from the first, 2 = from the
//                              second, etc.)
//
// The "1 contract per project" rule is enforced by a UNIQUE
// (company_id, project_id) index in migration 021. The user
// updates the contract terms via UpdateAsync; creating a new
// contract for the same project is rejected (use the existing
// one).
// ============================================================

/// <summary>
/// Full contract record returned by GET endpoints. Includes the
/// audit timestamps. Status is implicit (a contract exists or it
/// doesn't — the lifecycle is on the progress_billings, not the
/// contract itself).
/// </summary>
public record ContractDto(
    Guid Id,
    Guid CompanyId,
    Guid ProjectId,
    string? ContractNumber,
    decimal ContractValue,
    decimal AdvancePercent,
    decimal RetentionPercent,
    int RetentionStartBilling,
    DateTime? StartDate,
    DateTime? EndDate,
    string? Notes,
    // Sprint 53 — additional deductions (libyan construction contracts)
    decimal FinalInsurancePercent,        // e.g. 2% final performance bond
    decimal AdminFeePercent,              // e.g. 1.5% paid to owner
    DateTime? FinalInsuranceReleaseDate,  // when the insurance is released
    // Sprint 54 — additional multi-party context
    DateTime? SiteHandoverDate,           // تاريخ استلام الموقع
    decimal? OriginalContractValue,       // القيمة الأصلية قبل الأمر التعديلي
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

/// <summary>
/// Body for POST /api/projects/{id}/contract. The projectId is
/// taken from the URL (not the body) so the contract is always
/// created for the right project.
/// </summary>
public record CreateContractRequest(
    string? ContractNumber,
    decimal ContractValue,
    decimal AdvancePercent,
    decimal RetentionPercent,
    int RetentionStartBilling,
    DateTime? StartDate,
    DateTime? EndDate,
    string? Notes,
    // Sprint 53 — additional deductions
    decimal FinalInsurancePercent = 0m,
    decimal AdminFeePercent = 0m,
    DateTime? FinalInsuranceReleaseDate = null,
    // Sprint 54 — additional multi-party context
    DateTime? SiteHandoverDate = null,
    decimal? OriginalContractValue = null
);

/// <summary>
/// Body for PUT /api/contracts/{id}. All fields are required
/// because PUT is a full replacement (not a PATCH).
/// </summary>
public record UpdateContractRequest(
    string? ContractNumber,
    decimal ContractValue,
    decimal AdvancePercent,
    decimal RetentionPercent,
    int RetentionStartBilling,
    DateTime? StartDate,
    DateTime? EndDate,
    string? Notes,
    // Sprint 53 — additional deductions
    decimal FinalInsurancePercent = 0m,
    decimal AdminFeePercent = 0m,
    DateTime? FinalInsuranceReleaseDate = null,
    // Sprint 54 — additional multi-party context
    DateTime? SiteHandoverDate = null,
    decimal? OriginalContractValue = null
);

// ============================================================
// Sprint 36 — Progress Billings (المستخلصات)
// ============================================================
//
// A progress billing is the contractor's claim for work done in a
// period. It is a draft until the user reviews and approves it;
// on approval, BillingService.ApproveAsync creates:
//   - A POSTED sales invoice for the net amount
//   - A POSTED journal entry (DR AR 1103 / CR Sales 4101)
// And links both back via invoice_id / journal_entry_id.
//
// The status field follows:
//   DRAFT     → user is editing, no accounting effect
//   INVOICED  → approved, invoice + journal entry created
//   CANCELLED → user voided it; the invoice/entry are NOT created
// ============================================================

/// <summary>
/// One progress billing. The four amounts (gross, advance, retention,
/// net) are pre-computed by BillingService.CreateAsync — the UI
/// does NOT recompute them.
/// </summary>
public record ProgressBillingDto(
    Guid Id,
    Guid CompanyId,
    Guid ProjectId,
    Guid ContractId,
    string BillingNumber,
    DateTime BillingDate,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    decimal WorkCompletedPercent,
    decimal GrossAmount,
    decimal AdvanceDeducted,
    decimal RetentionDeducted,
    // Sprint 53 — additional deductions (mirrors the Libyan
    // construction contract settlement model). Order matches the
    // SELECT projection in BillingService + the BillingRow record.
    decimal FinalInsuranceDeducted,       // 2% final performance bond
    decimal AdminFeesDeducted,            // 1.5% admin fees to owner
    decimal OriginalContractDeduction,    // 15% one-time tax on first billing
    decimal NetAmount,                    // after ALL deductions
    string Status,                // "DRAFT" | "INVOICED" | "CANCELLED"
    Guid? InvoiceId,              // set after ApproveAsync
    Guid? JournalEntryId,         // set after ApproveAsync
    string? Notes,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

/// <summary>
/// Body for POST /api/projects/{id}/billings. The projectId comes
/// from the URL; the contractId tells the service which terms
/// (advance %, retention %) to use.
///
/// <para>
/// <b>Sprint 38 change</b>: the manual <c>WorkCompletedPercent</c>
/// field is gone. The % is now derived from the line items — the
/// caller passes <c>LineItems</c> (a list of {lineItemId, quantityThisPeriod})
/// and the service computes gross → net → work_completed_percent.
/// </para>
///
/// <para>
/// Backward-compat: <c>WorkCompletedPercent</c> is kept on the
/// DTO so legacy callers (Sprint 36 frontends) still compile. New
/// code should use <c>LineItems</c>. The service falls back to the
/// manual % if <c>LineItems</c> is null/empty AND a synthetic lump
/// line item exists for this contract (the migration 022 case).
/// </para>
/// </summary>
public record CreateBillingRequest(
    Guid ContractId,
    string BillingNumber,
    DateTime BillingDate,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    decimal? WorkCompletedPercent,
    string? Notes,
    List<CreateBillingLineItemRequest>? LineItems
);

/// <summary>
/// Body for POST /api/billings/{id}/approve. The user can override
/// the billing date (e.g. back-date for accounting period close)
/// and add notes that land on the resulting invoice + journal entry.
/// </summary>
public record ApproveBillingRequest(
    DateTime BillingDate,
    string? Notes
);

/// <summary>
/// Body for PUT /api/billings/{id}. Allowed only while the
/// billing is in DRAFT status — once approved the figures are
/// frozen in the invoice + journal entry and can only be changed
/// by reversing the whole flow.
///
/// <para>
/// <b>Sprint 38 change</b>: <c>WorkCompletedPercent</c> is now
/// optional (nullable) and <c>LineItems</c> is the canonical
/// input. If <c>LineItems</c> is provided, the % is recomputed
/// from the items. If <c>WorkCompletedPercent</c> is provided on
/// its own (no <c>LineItems</c>), the legacy path is used.
/// </para>
/// </summary>
public record UpdateBillingRequest(
    string? BillingNumber,
    DateTime? BillingDate,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    decimal? WorkCompletedPercent,
    string? Notes,
    List<CreateBillingLineItemRequest>? LineItems
);

/// <summary>
/// Work-in-Progress report for a project.
/// WIP = total costs (incurred but not yet billed)
///     − total billed (invoiced but not yet paid for work)
/// A positive WIP means we've spent more than we've billed
/// (typical for in-progress work). A negative WIP means we've
/// billed more than we've spent (typical for advance-heavy
/// contracts where the customer pre-pays).
/// </summary>
public record WipResponse(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    decimal TotalCosts,         // sum of journal lines on 54xx tagged with project + status='posted'
    decimal TotalBilled,        // sum of progress_billings.net_amount where status IN ('INVOICED','PAID')
    decimal WipAmount,          // TotalCosts - TotalBilled
    string WipStatus,           // "COSTS_EXCEED_BILLED" | "BILLED_EXCEED_COSTS" | "BALANCED"
    DateTime AsOfDate
);

/// <summary>
/// Client statement — the contractor's-eye view of a single project.
/// Tells the user:
///   - the original contract value
///   - how much has been billed (sum of net amounts on INVOICED billings)
///   - how much has been paid (sum of receipts applied to those invoices)
///   - how much is being held as retention
///   - how much advance payment is still on the books (un-earned)
///   - the outstanding net (billed - paid)
/// </summary>
public record ClientStatementResponse(
    Guid ProjectId,
    Guid? ContractId,
    decimal ContractValue,
    decimal TotalBilled,             // sum of net amounts (INVOICED billings)
    decimal TotalPaid,               // sum of receipts against the resulting invoices
    decimal RetentionHeld,           // sum of retention_deducted (still on the books)
    decimal AdvanceOutstanding,      // advance_deducted sum across billings (the "advance" we collected)
    decimal NetOutstanding          // TotalBilled - TotalPaid
);

// ============================================================
// Sprint 38 — BOQ (Bill of Quantities) + Variations
// ============================================================
//
// A contract's BOQ is a list of measurable items. Each item has a
// unit, a total quantity, and a unit price — the contract_value
// is the SUM of item.total_price. The user manages the BOQ in the
// UI; the service enforces that:
//   - total_price = quantity * unit_price (server-side)
//   - the line item cannot be deleted if any non-cancelled billing
//     has claimed it
//   - reordering is safe to retry (idempotent UPDATE in a tx)
//
// Variations (أوامر التغيير) are out-of-band scope changes. They
// are tracked in their own table with their own items, and become
// effective (add to / subtract from the contract value) only after
// the variation is APPROVED. Pre-approval, the items can be
// freely edited or removed.
// ============================================================

/// <summary>
/// One BOQ line item (مقايسة بند). Read view: includes the
/// derived fields <c>QuantityBilledSoFar</c> and
/// <c>QuantityRemaining</c> so the UI can show the %-per-item
/// progress without a second round-trip.
/// </summary>
public record ContractLineItemDto(
    Guid Id,
    Guid CompanyId,
    Guid ContractId,
    int LineNumber,
    string Description,
    string Unit,
    string? CustomUnit,
    decimal Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    decimal QuantityBilledSoFar,      // SUM(quantity_cumulative) from billing_line_items
    decimal QuantityRemaining,         // Quantity - QuantityBilledSoFar
    string? Notes,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

/// <summary>
/// Body for POST /api/contracts/{id}/line-items. The line number
/// is auto-assigned (max + 1) — the UI does not pass it.
/// </summary>
public record CreateLineItemRequest(
    string Description,
    string Unit,
    string? CustomUnit,
    decimal Quantity,
    decimal UnitPrice,
    string? Notes
);

/// <summary>
/// Body for PUT /api/line-items/{id}. All four content fields are
/// required (full replacement). total_price is recomputed server-side.
/// </summary>
public record UpdateLineItemRequest(
    string Description,
    string Unit,
    string? CustomUnit,
    decimal Quantity,
    decimal UnitPrice,
    string? Notes
);

/// <summary>
/// Body for POST /api/contracts/{id}/line-items/reorder. The list
/// contains the line item ids in the desired display order; the
/// service reassigns line_number = (index + 1).
/// </summary>
public record ReorderLineItemsRequest(List<Guid> LineItemIds);

/// <summary>
/// One billing_line_item — the claim of "we did X units of this
/// item in this billing". The amount column is derived
/// (quantity_cumulative * unit_price). unit_price is a snapshot
/// from the line item at billing-creation time, so later edits to
/// the line item's unit_price do NOT retroactively change existing
/// billings.
/// </summary>
public record BillingLineItemDto(
    Guid Id,
    Guid BillingId,
    Guid LineItemId,
    int LineNumber,
    string Description,
    string Unit,
    string? CustomUnit,
    decimal QuantityThisPeriod,
    decimal QuantityPrevious,
    decimal QuantityCumulative,
    decimal UnitPrice,
    decimal Amount,
    string? Notes
);

/// <summary>
/// Body for posting a single line item on a billing. The caller
/// supplies only the lineItemId and the new period quantity; the
/// service computes previous/cumulative and amount.
/// </summary>
public record CreateBillingLineItemRequest(
    Guid LineItemId,
    decimal QuantityThisPeriod,
    string? Notes
);

/// <summary>
/// Read-only preview of what the billing_line_items WOULD look
/// like if the user submitted these quantities. Returned by the
/// preview endpoint so the UI can show live totals before
/// committing.
/// </summary>
public record BillingLineItemPreview(
    Guid LineItemId,
    int LineNumber,
    string Description,
    string Unit,
    decimal Quantity,
    decimal QuantityPrevious,
    decimal UnitPrice,
    decimal ProposedThisPeriod,
    decimal ProposedCumulative,
    decimal ProposedAmount
);

/// <summary>
/// One contract variation (أمر تغيير). Includes the items array
/// so the UI can show the variation detail page in one round-trip.
/// </summary>
public record ContractVariationDto(
    Guid Id,
    Guid CompanyId,
    Guid ContractId,
    int VariationNumber,
    string Description,
    DateTime VariationDate,
    string Status,                   // DRAFT | APPROVED | REJECTED
    DateTime? ApprovedAt,
    Guid? ApprovedBy,
    string? Notes,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    List<ContractVariationItemDto> Items
);

/// <summary>
/// One item inside a variation. is_addition=true means this row
/// adds to the effective contract value; is_addition=false means
/// it subtracts (omitted work).
/// </summary>
public record ContractVariationItemDto(
    Guid Id,
    Guid VariationId,
    int LineNumber,
    string Description,
    string Unit,
    string? CustomUnit,
    decimal Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    bool IsAddition,
    string? Notes
);

/// <summary>
/// Body for POST /api/contracts/{id}/variations. Creates the
/// variation in DRAFT status. Items are added separately via
/// POST /api/variations/{id}/items.
/// </summary>
public record CreateVariationRequest(
    string Description,
    DateTime VariationDate,
    string? Notes
);

/// <summary>
/// Body for POST /api/variations/{id}/items. Add a single line to
/// a DRAFT variation. The line number is auto-assigned.
/// </summary>
public record AddVariationItemRequest(
    string Description,
    string Unit,
    string? CustomUnit,
    decimal Quantity,
    decimal UnitPrice,
    bool IsAddition,
    string? Notes
);

/// <summary>
/// Body for PUT /api/variation-items/{id}. Full replacement of
/// the editable fields on a DRAFT variation item.
/// </summary>
public record UpdateVariationItemRequest(
    string Description,
    string Unit,
    string? CustomUnit,
    decimal Quantity,
    decimal UnitPrice,
    bool IsAddition,
    string? Notes
);

/// <summary>
/// Body for POST /api/variations/{id}/approve. The user can
/// override the approval date (e.g. back-date to the variation_date
/// for accounting period close).
/// </summary>
public record ApproveVariationRequest(DateTime? ApprovedAt);

/// <summary>
/// Body for POST /api/contracts/{id}/line-items/import-excel.
/// The file is sent as multipart/form-data; the endpoint reads
/// the bytes and ClosedXML parses the workbook. The expected
/// columns are: line_number, description, unit, quantity, unit_price.
/// </summary>
public record ImportLineItemsRequest(
    string FileName,
    string ContentType,
    byte[] Content
);

/// <summary>
/// Result of an Excel / clipboard import. TotalRows is the number
/// of data rows seen (excluding the header); SuccessCount is the
/// number of rows that parsed + validated cleanly; ErrorCount is
/// the rest. The caller decides what to do with Errors (the UI
/// usually shows them in a red toast).
/// </summary>
public record ImportLineItemsResult(
    int TotalRows,
    int SuccessCount,
    int ErrorCount,
    List<ImportedLineItem> Imported,
    List<string> Errors
);

/// <summary>
/// One row that parsed cleanly during import. The lineNumber is
/// the row's position in the file (1-based, after the header);
/// the service reassigns line numbers on insert to keep them
/// unique per contract.
/// </summary>
public record ImportedLineItem(
    int LineNumber,
    string Description,
    string Unit,
    string? CustomUnit,
    decimal Quantity,
    decimal UnitPrice,
    decimal TotalPrice
);

/// <summary>
/// Body for POST /api/contracts/{id}/effective-value. There is
/// no body — the response is the effective contract value (=
/// contract.contract_value + sum of approved variation items where
/// is_addition=true - sum where is_addition=false). Wrapped in a
/// DTO for forward compatibility (we may add currency, dates,
/// etc. later).
/// </summary>
public record EffectiveContractValueResponse(
    Guid ContractId,
    decimal ContractValue,
    decimal ApprovedVariationsNet,
    decimal EffectiveValue,
    int ApprovedVariationCount
);
