namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 56 — Per-line progress. Mirrors the contract_line_item_progress
/// table (created in migration 030) plus the joined BOQ line info.
/// </summary>
public record LineItemProgressDto(
    Guid Id,
    Guid LineItemId,
    int LineNumber,
    string Description,
    string Unit,
    decimal ContractQuantity,
    decimal UnitPrice,
    decimal QuantityDone,
    decimal ProgressPercent,
    decimal AmountCompleted,    // = quantityDone × unitPrice
    DateTime LastUpdated,
    bool IsManualOverride,
    string? Notes
);

/// <summary>
/// Sprint 56 — Project Technical Report (التقرير الفني).
/// Auto-computed from line item progress + billings.
/// </summary>
public record ProjectProgressDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    // Sprint 56 fields
    decimal PhysicalProgressPercent,
    decimal FinancialProgressPercent,
    string ScheduleStatus,         // on_track | delayed | ahead | no_schedule | stopped
    string ExecutionStatus,        // completed | in_progress | stopped
    DateTime? TechReportDate,
    // Aggregated
    int TotalLineItems,
    int CompletedLineItems,        // progress_percent >= 100
    decimal TotalContractValue,
    decimal TotalAmountCompleted,
    List<LineItemProgressDto> LineItems
);

public record UpdateProjectProgressRequest(
    decimal PhysicalProgressPercent,
    decimal FinancialProgressPercent,
    string ScheduleStatus,
    string ExecutionStatus,
    DateTime? TechReportDate
);

public record UpdateLineItemProgressRequest(
    decimal ProgressPercent,
    decimal QuantityDone,
    bool IsManualOverride,
    string? Notes
);
