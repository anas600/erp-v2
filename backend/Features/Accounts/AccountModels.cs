namespace ErpV2.Features.Accounts;

public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense
}

public enum AccountNature
{
    Debit,
    Credit
}

public record AccountDto(
    Guid Id,
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    Guid? ParentId,
    string AccountType,
    string Nature,
    bool IsActive,
    decimal Balance
);

public record CreateAccountRequest(
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    Guid? ParentId,
    string AccountType,
    string Nature
);

public record UpdateAccountRequest(
    string Name,
    string? NameAr,
    bool IsActive
);
