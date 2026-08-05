namespace ErpV2.Features.CostCenters;

/// <summary>
/// A cost center is a way to slice the company's expenses: by project
/// (e.g. "Project Alpha"), by department (e.g. "HR"), or by activity
/// (e.g. "Marketing Campaign"). Journal lines can optionally be tagged
/// with a cost center so reports can break out P&L by dimension.
///
/// Hierarchical: a cost center can have a parent cost center
/// (e.g. a department "Engineering" might have a child "Engineering -
/// Backend"). The `parent_id` self-FK handles that.
///
/// Lifecycle: soft-deleted (is_active = false), never hard-deleted
/// once any journal line references it.
/// </summary>
public record CostCenterDto(
    Guid Id,
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    string Type,
    Guid? ProjectId,
    Guid? ParentId,
    bool IsActive,
    DateTime CreatedAt
);

/// <summary>
/// Create a new cost center. The unique key is (company_id, code) —
/// you cannot have two cost centers with the same code in the same
/// company. Code is mandatory and treated as the user-facing
/// identifier (Name is the display label, NameAr is the Arabic
/// display label, optional).
/// </summary>
public record CreateCostCenterRequest(
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    string Type,
    Guid? ProjectId = null,
    Guid? ParentId = null
);

/// <summary>
/// Update an existing cost center. Only the mutable fields are
/// exposed — code, company, type, projectId, and parentId are
/// structural and cannot be changed after creation (changing them
/// would invalidate historical journal lines that reference this
/// cost center).
/// </summary>
public record UpdateCostCenterRequest(
    string? Name,
    string? NameAr,
    bool? IsActive
);
