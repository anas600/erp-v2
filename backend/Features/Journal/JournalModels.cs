namespace ErpV2.Features.Journal;

public record JournalEntryDto(
    Guid Id,
    Guid CompanyId,
    string EntryNumber,
    DateTime EntryDate,
    string? Narration,
    string Status,
    string? Source,
    Guid? RuleId,
    Guid? ReversesEntryId,           // The original entry this one reverses (null for normal entries)
    string? ReversesEntryNumber,     // Human-readable "JV-2026-0001" form of the above
    Guid? CreatedBy,
    DateTime CreatedAt,
    DateTime? PostedAt,
    // Sprint 35: optional project tag (set when the user allocates
    // a JE to a project, either via bulk endpoint or directly on
    // creation). P&L reports use this column to sum cost lines.
    Guid? ProjectId,
    List<JournalLineDto> Lines
);

public record JournalLineDto(
    Guid Id,
    Guid AccountId,
    string? AccountCode,
    string? AccountName,
    decimal Debit,
    decimal Credit,
    string? Description,
    int LineNumber
);

public record CreateJournalEntryRequest(
    Guid CompanyId,
    DateTime EntryDate,
    string? Narration,
    List<CreateJournalLineRequest> Lines,
    string? Source = null,            // "manual" | "invoice" | "rule:{ruleId}" | "reverse" — defaults to "manual" in the service
    Guid? RuleId = null,               // FK back to business_rules.id when Source starts with "rule:"
    Guid? ReversesEntryId = null,      // FK back to journal_entries.id when Source = "reverse"
    // Sprint 35: optional project tag. Default null so the
    // hundreds of existing callers (rule pipeline, voucher
    // posting, etc.) keep working unchanged. P&L reports use
    // this column to attribute cost lines to projects.
    Guid? ProjectId = null
);

public record CreateJournalLineRequest(
    Guid AccountId,
    decimal Debit,
    decimal Credit,
    string? Description,
    Guid? CostCenterId = null
);

public record PostEntryRequest(Guid EntryId);
