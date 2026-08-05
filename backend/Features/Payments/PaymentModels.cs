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
    /// <summary>
    /// Sprint 25 — optional FK to the invoice this payment settles
    /// (supplier's bill). NULL for advance payments. See the
    /// matching doc on ReceiptVoucherDto for the receipt side.
    /// </summary>
    Guid? InvoiceId,
    string? InvoiceNumber,
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
    Guid? InvoiceId = null
);
