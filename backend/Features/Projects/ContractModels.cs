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
    string? Notes
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
    string? Notes
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
    decimal NetAmount,
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
/// </summary>
public record CreateBillingRequest(
    Guid ContractId,
    string BillingNumber,
    DateTime BillingDate,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    decimal WorkCompletedPercent,
    string? Notes
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
/// </summary>
public record UpdateBillingRequest(
    string? BillingNumber,
    DateTime? BillingDate,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    decimal? WorkCompletedPercent,
    string? Notes
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
