namespace ErpV2.Features.Auth;

public record LoginRequest(string Email, string Password);

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    UserInfo User,
    List<CompanyInfo> Companies
);

public record UserInfo(
    Guid Id,
    string Email,
    string? FullName,
    string? FullNameAr,
    bool IsSuperAdmin
);

public record CompanyInfo(
    Guid Id,
    string Code,
    string Name,
    string? NameAr,
    Guid RoleId,
    string RoleName,
    bool IsPrimary
);

public record SwitchCompanyRequest(Guid CompanyId);
public record SwitchCompanyResponse(string AccessToken, CompanyInfo Company);
