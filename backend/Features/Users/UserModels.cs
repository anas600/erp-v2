namespace ErpV2.Features.Users;

public record UserDto(
    Guid Id,
    string Email,
    string? FullName,
    string? FullNameAr,
    bool IsSuperAdmin,
    bool IsActive,
    DateTime CreatedAt,
    List<UserCompanyMembership> Companies
);

public record UserCompanyMembership(
    Guid CompanyId,
    string CompanyCode,
    string CompanyName,
    string CompanyNameAr,
    Guid RoleId,
    string RoleName,
    string RoleNameAr,
    bool IsPrimary
);

public record CreateUserRequest(
    string Email,
    string Password,
    string? FullName,
    string? FullNameAr,
    List<CreateUserCompanyRequest> Companies
);

public record CreateUserCompanyRequest(
    Guid CompanyId,
    Guid RoleId,
    bool IsPrimary
);

public record UpdateUserRequest(
    string? FullName,
    string? FullNameAr,
    bool? IsActive,
    List<UpdateUserCompanyRequest>? Companies
);

public record UpdateUserCompanyRequest(
    Guid CompanyId,
    Guid RoleId,
    bool IsPrimary
);

public record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword
);
