namespace ErpV2.Features.Payments;

public record PaymentVoucherDto(
    Guid Id,
    Guid CompanyId,
    string VoucherNumber,
    DateTime VoucherDate,
    Guid ContactId,
    string ContactName,
    string ContactCode,
    decimal Amount,
    string PaymentMethod,
    Guid? BankAccountId,
    string? CheckNumber,
    DateTime? CheckDate,
    string? Reference,
    string? Narration,
    string Status,
    DateTime? PostedAt,
    Guid? JournalEntryId,
    // Sprint 25 — bi-directional link to the purchase invoice the
    // payment cleared. NULL when the payment is a general settlement
    // with no exact-amount match, or was posted before this feature.
    Guid? InvoiceId,
    string? InvoiceNumber,
    string? InvoiceStatus,
    DateTime CreatedAt,
    Guid? CreatedBy,
    string? CreatedByName
);

public record CreatePaymentVoucherRequest(
    Guid CompanyId,
    DateTime VoucherDate,
    Guid ContactId,
    decimal Amount,
    string PaymentMethod,
    Guid? BankAccountId,
    string? CheckNumber,
    DateTime? CheckDate,
    string? Reference,
    string? Narration,
    // Sprint 25 — optional pre-link. The UI's "الفاتورة" dropdown
    // sends this when the user explicitly picks an invoice. If
    // omitted, PostAsync runs the auto-link heuristic (exact-amount
    // match against unpaid purchase invoices for the same contact).
    Guid? InvoiceId = null
);
