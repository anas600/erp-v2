namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 35 — extended with project type, customer, contract value,
/// manager, location, dates, and the explicit updated_at stamp.
/// All new fields are nullable so legacy projects keep working.
/// </summary>
public record ProjectDto(
    Guid Id,
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    string? Description,
    string Status,
    DateTime? StartDate,
    DateTime? EndDate,
    decimal Budget,
    decimal ActualCost,
    string? Notes,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    List<MilestoneDto> Milestones,
    // ---------- Sprint 35 additions ----------
    string? Type,                // 'construction' | 'service' | 'supply' | 'consulting' | 'other' (free-text)
    Guid? CustomerId,            // optional FK to contacts (the project owner)
    string? CustomerName,        // denormalized for display (joined from contacts)
    decimal ContractValue,       // total contract value (decimal 18,3)
    DateTime? ExpectedEndDate,   // planned end
    DateTime? ActualEndDate,     // actual end (set when status flips to 'completed')
    string? ProjectManager,      // free-text manager name
    string? Location,            // free-text location / site
    // ---------- Sprint 54 additions (4-party project model) ----------
    Guid? ContractorId,          // الجهة المنفذة (FK contacts, type='contractor')
    string? ContractorName,      // denormalized
    Guid? ConsultantId,          // الجهة المشرفة (FK contacts, type='consultant')
    string? ConsultantName,      // denormalized
    // ---------- Sprint 56 additions (Technical Report) ----------
    decimal PhysicalProgressPercent,    // 0-100
    decimal FinancialProgressPercent,   // 0-100 (auto-calc from billings)
    string? ScheduleStatus,             // on_track | delayed | ahead | no_schedule | stopped
    string? ExecutionStatus,            // completed | in_progress | stopped
    DateTime? TechReportDate            // last update of the report
);

public record MilestoneDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? NameAr,
    string? Description,
    decimal Amount,
    string Status,
    DateTime? TargetDate,
    DateTime? CompletedAt,
    int OrderIndex
);

/// <summary>
/// Sprint 35 — adds Type, CustomerId, ContractValue, ExpectedEndDate,
/// ProjectManager, Location. Notes was already there.
/// </summary>
public record CreateProjectRequest(
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    string? Description,
    DateTime? StartDate,
    DateTime? EndDate,
    decimal Budget,
    string? Notes,
    // ---------- Sprint 35 additions ----------
    string? Type = null,
    Guid? CustomerId = null,
    decimal ContractValue = 0,
    DateTime? ExpectedEndDate = null,
    string? ProjectManager = null,
    string? Location = null,
    // ---------- Sprint 54 additions (4-party model) ----------
    Guid? ContractorId = null,
    Guid? ConsultantId = null,
    // ---------- Sprint 56 additions (Technical Report) ----------
    decimal PhysicalProgressPercent = 0,
    decimal FinancialProgressPercent = 0,
    string? ScheduleStatus = null,
    string? ExecutionStatus = null,
    DateTime? TechReportDate = null
);

/// <summary>
/// Sprint 35 — Status is now part of the update payload (so the
/// endpoint can move a project to 'completed' / 'on_hold' etc).
/// All other new fields are nullable so partial updates work.
/// </summary>
public record UpdateProjectRequest(
    string Name,
    string? NameAr,
    string? Description,
    string Status,
    DateTime? StartDate,
    DateTime? EndDate,
    decimal Budget,
    string? Notes,
    // ---------- Sprint 35 additions ----------
    string? Type = null,
    Guid? CustomerId = null,
    decimal ContractValue = 0,
    DateTime? ExpectedEndDate = null,
    DateTime? ActualEndDate = null,
    string? ProjectManager = null,
    string? Location = null,
    // ---------- Sprint 54 additions (4-party model) ----------
    Guid? ContractorId = null,
    Guid? ConsultantId = null,
    // ---------- Sprint 56 additions (Technical Report) ----------
    decimal PhysicalProgressPercent = 0,
    decimal FinancialProgressPercent = 0,
    string? ScheduleStatus = null,
    string? ExecutionStatus = null,
    DateTime? TechReportDate = null
);

public record CreateMilestoneRequest(
    string Name,
    string? NameAr,
    string? Description,
    decimal Amount,
    DateTime? TargetDate,
    int OrderIndex
);

public record CompleteMilestoneRequest(Guid MilestoneId);

// ============================================================
// Sprint 35 — P&L DTOs
// ============================================================

/// <summary>
/// One row in a project's cost breakdown. Grouped by the COA code
/// (5401-5407 are the standard expense range in our 4-level COA).
/// </summary>
public record CostCategoryPnL(
    string Category,        // human label, e.g. "مواد", "أجور", "إيجار"
    string AccountCode,     // COA code, e.g. "5401"
    decimal Amount          // total expense in this category
);

/// <summary>
/// Per-project P&L report. Computed from POSTED sales invoices
/// (revenue) and journal lines on accounts 5401-5407 (costs).
/// </summary>
public record ProjectPnLResponse(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    decimal TotalRevenue,
    List<CostCategoryPnL> CostsByCategory,
    decimal TotalCosts,
    decimal GrossProfit,    // TotalRevenue - TotalCosts
    decimal ProfitMargin,   // (GrossProfit / TotalRevenue) * 100; 0 if revenue == 0
    int InvoiceCount,       // number of sales invoices tagged with this project
    int JournalEntryCount   // number of JEs tagged with this project
);

/// <summary>
/// Allocation request body for the bulk endpoints.
/// Both invoice and journal-entry allocation share the same shape
/// (a list of GUIDs), so we use one record for both.
/// </summary>
public record AllocateRequest(List<Guid> InvoiceIds);

public record AllocateJournalEntriesRequest(List<Guid> JournalEntryIds);
