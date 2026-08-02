namespace ErpV2.Features.Companies;

public record CompanyDto(
    Guid Id,
    string Code,
    string Name,
    string? NameAr,
    Guid? ParentId,
    bool IsHolding,
    string BaseCurrency,
    bool IsActive,
    DateTime CreatedAt
);

public record CreateCompanyRequest(
    string Code,
    string Name,
    string? NameAr,
    Guid? ParentId,
    bool IsHolding,
    string? BaseCurrency
);

public record UpdateCompanyRequest(
    string Name,
    string? NameAr,
    bool IsActive,
    string? BaseCurrency
);
