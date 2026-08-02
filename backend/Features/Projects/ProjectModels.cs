namespace ErpV2.Features.Projects;

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
    List<MilestoneDto> Milestones
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

public record CreateProjectRequest(
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    string? Description,
    DateTime? StartDate,
    DateTime? EndDate,
    decimal Budget,
    string? Notes
);

public record UpdateProjectRequest(
    string Name,
    string? NameAr,
    string? Description,
    string Status,
    DateTime? StartDate,
    DateTime? EndDate,
    decimal Budget,
    string? Notes
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
