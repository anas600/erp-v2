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
    // Sprint 25 — bi-directional link to the sales invoice the
    // receipt settled. NULL when (a) the receipt is a general
    // deposit / on-account payment, (b) no exact-amount match
    // existed at post time, or (c) the receipt was posted before
    // this feature shipped (legacy rows).
    Guid? InvoiceId,
    // Sprint 25 — denormalised display fields. The receipts list
    // page shows "INV-S-2026-0008" next to the voucher number so
    // the user can see at a glance which invoice a receipt cleared.
    // Both fields are nullable because not every receipt is linked.
    string? InvoiceNumber,
    string? InvoiceStatus,
    DateTime CreatedAt,
    Guid? CreatedBy,
    string? CreatedByName
);

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
    // Sprint 25 — optional pre-link. The UI's "الفاتورة" dropdown
    // sends this when the user explicitly picks an invoice. If
    // omitted, PostAsync runs the auto-link heuristic (exact-amount
    // match against unpaid sales invoices for the same contact).
    Guid? InvoiceId = null
);
