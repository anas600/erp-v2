namespace ErpV2.Features.Receipts;

public record ReceiptVoucherDto(
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
    /// Sprint 25 — optional FK to the invoice this receipt settles. NULL
    /// for advance payments that aren't tied to a specific invoice. When
    /// set, the receipt's PostAsync calls InvoiceService.ApplyPaymentAsync
    /// to bump invoices.amount_paid.
    /// </summary>
    Guid? InvoiceId,
    string? InvoiceNumber,
    DateTime CreatedAt,
    Guid? CreatedBy,
    string? CreatedByName
);

/// <summary>
/// Sprint 25 — <see cref="InvoiceId"/> is the new optional field that
/// links a receipt to a specific invoice. When set, the receipt
/// effectively pays the invoice (partially or fully). When null, the
/// receipt is an advance payment that just credits the contact's
/// sub-ledger without affecting any invoice.
/// </summary>
public record CreateReceiptVoucherRequest(
    Guid CompanyId,
    DateTime VoucherDate,
    Guid ContactId,
    decimal Amount,
    string PaymentMethod,   // 'cash' | 'bank' | 'check'
    Guid? BankAccountId,
    string? CheckNumber,
    DateTime? CheckDate,
    string? Reference,
    string? Narration,
    Guid? InvoiceId = null
);
