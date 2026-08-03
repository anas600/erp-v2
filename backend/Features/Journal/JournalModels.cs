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
    Guid? CreatedBy,
    DateTime CreatedAt,
    DateTime? PostedAt,
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
    string? Source = null,   // "manual" | "invoice" | "rule:{ruleId}" — defaults to "manual" in the service
    Guid? RuleId = null      // FK back to business_rules.id when Source starts with "rule:"
);

public record CreateJournalLineRequest(
    Guid AccountId,
    decimal Debit,
    decimal Credit,
    string? Description
);

public record PostEntryRequest(Guid EntryId);
