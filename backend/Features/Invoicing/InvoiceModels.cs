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
    List<CreateInvoiceLineRequest> Lines
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
