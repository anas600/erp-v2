using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Accounts;

public class AccountService
{
    private readonly IDbConnectionFactory _db;

    public AccountService(IDbConnectionFactory db) => _db = db;

    public async Task<List<AccountDto>> GetByCompanyAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<AccountRow>(@"
            SELECT id, company_id, code, name, name_ar, parent_id, account_type, nature, is_active, balance
            FROM accounts
            WHERE company_id = @companyId
            ORDER BY code;",
            new { companyId });
        return rows.Select(Map).ToList();
    }

    public async Task<AccountDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<AccountRow>(@"
            SELECT id, company_id, code, name, name_ar, parent_id, account_type, nature, is_active, balance
            FROM accounts WHERE id = @id;",
            new { id });
        return row is null ? null : Map(row);
    }

    public async Task<AccountDto> CreateAsync(CreateAccountRequest req)
    {
        // Validate account_type
        var validTypes = new[] { "Asset", "Liability", "Equity", "Revenue", "Expense" };
        if (!validTypes.Contains(req.AccountType))
            throw new ArgumentException($"AccountType must be one of: {string.Join(", ", validTypes)}");

        // Validate nature
        var validNatures = new[] { "Debit", "Credit" };
        if (!validNatures.Contains(req.Nature))
            throw new ArgumentException($"Nature must be one of: {string.Join(", ", validNatures)}");

        using var conn = _db.CreateConnection();
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO accounts (id, company_id, code, name, name_ar, parent_id, account_type, nature, is_active, balance)
            VALUES (@id, @companyId, @code, @name, @nameAr, @parentId, @accountType, @nature, true, 0);",
            new
            {
                id, companyId = req.CompanyId, code = req.Code, name = req.Name, nameAr = req.NameAr,
                parentId = req.ParentId, accountType = req.AccountType, nature = req.Nature
            });

        return (await GetByIdAsync(id))!;
    }

    public async Task<AccountDto?> UpdateAsync(Guid id, UpdateAccountRequest req)
    {
        using var conn = _db.CreateConnection();
        var rowsAffected = await conn.ExecuteAsync(@"
            UPDATE accounts
            SET name = @name, name_ar = @nameAr, is_active = @isActive
            WHERE id = @id;",
            new { id, name = req.Name, nameAr = req.NameAr, isActive = req.IsActive });
        return rowsAffected == 0 ? null : await GetByIdAsync(id);
    }

    public async Task<List<AccountDto>> GetTreeAsync(Guid companyId)
    {
        var all = await GetByCompanyAsync(companyId);
        // Return flat list with parent reference (frontend builds the tree)
        return all;
    }

    private static AccountDto Map(AccountRow r) => new(
        r.id, r.company_id, r.code, r.name, r.name_ar, r.parent_id,
        r.account_type, r.nature, r.is_active, r.balance);

    private record AccountRow(
        Guid id, Guid company_id, string code, string name, string? name_ar,
        Guid? parent_id, string account_type, string nature, bool is_active, decimal balance);
}
