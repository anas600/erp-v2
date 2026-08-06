namespace ErpV2.Features.Invoicing;

public enum InvoiceType { Purchase, Sales }

public enum InvoiceStatus { Draft, Posted, Paid, Cancelled }

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
    decimal AmountPaid,              // Sprint 25: payments applied so far
    DateTime? PaidAt,                // Sprint 25: when amount_paid first reached total
    List<InvoiceLineDto> Lines
)
{
    // Sprint 25 — convenience for the aging report + UI. The
    // Outstanding field is computed from Total - AmountPaid and is
    // NEVER read back from the database (no Outstanding column in
    // invoices). Frontend can display it directly.
    public decimal Outstanding => Total - AmountPaid;
}

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
