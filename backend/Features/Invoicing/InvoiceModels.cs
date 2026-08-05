namespace ErpV2.Features.Invoicing;

public enum InvoiceType { Purchase, Sales }

/// <summary>
/// Invoice lifecycle states.
///
/// Sprint 25 settlement: the `Posted → PartiallyPaid → Paid` transitions are
/// driven by `ApplyPaymentAsync` (in InvoiceService), which increments
/// `invoices.amount_paid` and recomputes the status. `Paid` also stamps
/// `fully_paid_at` so the report can show "settled on" without joining
/// payment vouchers.
///
/// `Draft → Posted` is the posting transition (unchanged from Sprint 14+).
/// `Cancelled` is a terminal state for voided invoices; the rules engine
/// never creates a settlement voucher for a cancelled invoice.
/// </summary>
public enum InvoiceStatus { Draft, Posted, PartiallyPaid, Paid, Cancelled }

public record InvoiceDto(
    Guid Id,
    Guid CompanyId,
    string InvoiceNumber,
    string InvoiceType,
    DateTime InvoiceDate,
    string PartyName,
    string? PartyNameAr,
    string? PartyTaxId,
    string? Notes,
    decimal SubTotal,
    decimal TaxAmount,
    decimal Total,
    string Status,
    DateTime CreatedAt,
    DateTime? PostedAt,
    Guid? IntercompanyCompanyId,     // Sprint 24: sister-company target for the mirror invoice. NULL for intra-company invoices.
    decimal AmountPaid,              // Sprint 25: cumulative paid amount; drives the status transitions.
    DateTime? FullyPaidAt,           // Sprint 25: stamped when status flips to 'paid'. NULL otherwise.
    List<InvoiceLineDto> Lines
);

public record InvoiceLineDto(
    Guid Id,
    Guid? AccountId,
    string? AccountCode,
    string? AccountName,
    Guid? ProductId,
    string? ProductCode,
    string? ProductName,
    string? ProductNameAr,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate,
    decimal Amount,
    decimal LineTotalWithTax,
    int LineNumber
);

public record CreateInvoiceRequest(
    Guid CompanyId,
    string InvoiceType,        // "purchase" or "sales"
    DateTime InvoiceDate,
    string PartyName,
    string? PartyNameAr,
    string? PartyTaxId,
    string? Notes,
    decimal TaxRate,            // global tax rate, e.g. 0 or 0.15
    Guid? IntercompanyCompanyId = null,  // Sprint 24: optional sister-company id. When set, posting the invoice also creates a mirror in that company.
    List<CreateInvoiceLineRequest> Lines = null!
);

public record CreateInvoiceLineRequest(
    Guid? AccountId,           // legacy: GL-account line
    Guid? ProductId,           // new: product-based line (auto-fills description, unitPrice, taxRate)
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal? TaxRate           // per-line override (otherwise inherits from invoice or product)
);

public record PostInvoiceRequest(Guid InvoiceId);

/// <summary>
/// DTO for the contact's outstanding invoice list. Returned by
/// ContactStatementEndpoints.GetInvoicesAsync. Mirrors the
/// InvoiceDto shape but trimmed to the columns the UI needs.
/// </summary>
public record ContactInvoiceDto(
    Guid InvoiceId,
    string Number,
    DateTime Date,
    string Type,                 // 'sales' or 'purchase'
    decimal Total,
    decimal AmountPaid,
    decimal Outstanding,         // total - amount_paid
    string Status,               // 'posted' | 'partiallypaid' | 'paid'
    int AgeDays                  // days since invoice_date, as of request time
);

/// <summary>
/// Intercompany Pair DTO — Sprint 24.
///
/// A pair represents a single logical transaction that lives in the
/// books of two companies simultaneously. Example: HOLD issues a
/// sales invoice to CO-A. The pair has:
///   - primaryInvoiceId : the invoice HOLD created (sales, 1000 LYD)
///   - mirrorInvoiceId  : the invoice CO-A auto-created (purchase, 1000 LYD)
///   - primaryCompanyId : HOLD
///   - mirrorCompanyId  : CO-A
///   - amount, currency : the agreed amount (1000 LYD)
///   - status           : pending → posted → reversed lifecycle
///   - createdAt        : when the pair was created
/// </summary>
public record IntercompanyPairDto(
    Guid Id,
    Guid PrimaryInvoiceId,
    Guid? MirrorInvoiceId,
    Guid PrimaryCompanyId,
    Guid MirrorCompanyId,
    decimal Amount,
    string Currency,
    string Status,
    DateTime CreatedAt
);
