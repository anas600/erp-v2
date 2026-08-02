using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Companies;

public class CompanyService
{
    private readonly IDbConnectionFactory _db;

    public CompanyService(IDbConnectionFactory db) => _db = db;

    public async Task<List<CompanyDto>> GetAllAsync()
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<CompanyRow>(@"
            SELECT id, code, name, name_ar, parent_id, is_holding, base_currency, is_active, created_at
            FROM companies
            ORDER BY is_holding DESC, name;");
        return rows.Select(Map).ToList();
    }

    public async Task<List<CompanyDto>> GetForUserAsync(Guid userId, bool isSuperAdmin)
    {
        using var conn = _db.CreateConnection();
        var sql = isSuperAdmin
            ? "SELECT id, code, name, name_ar, parent_id, is_holding, base_currency, is_active, created_at FROM companies ORDER BY is_holding DESC, name;"
            : @"SELECT c.id, c.code, c.name, c.name_ar, c.parent_id, c.is_holding, c.base_currency, c.is_active, c.created_at
                FROM companies c
                JOIN user_companies uc ON uc.company_id = c.id
                WHERE uc.user_id = @userId
                ORDER BY c.is_holding DESC, c.name;";
        var rows = await conn.QueryAsync<CompanyRow>(sql, new { userId });
        return rows.Select(Map).ToList();
    }

    public async Task<CompanyDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<CompanyRow>(@"
            SELECT id, code, name, name_ar, parent_id, is_holding, base_currency, is_active, created_at
            FROM companies WHERE id = @id;",
            new { id });
        return row is null ? null : Map(row);
    }

    public async Task<CompanyDto> CreateAsync(CreateCompanyRequest req)
    {
        using var conn = _db.CreateConnection();
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO companies (id, code, name, name_ar, parent_id, is_holding, base_currency, is_active)
            VALUES (@id, @code, @name, @nameAr, @parentId, @isHolding, @baseCurrency, true);",
            new
            {
                id, code = req.Code, name = req.Name, nameAr = req.NameAr,
                parentId = req.ParentId, isHolding = req.IsHolding,
                baseCurrency = req.BaseCurrency ?? "LYD"
            });

        return (await GetByIdAsync(id))!;
    }

    public async Task<CompanyDto?> UpdateAsync(Guid id, UpdateCompanyRequest req)
    {
        using var conn = _db.CreateConnection();
        var rowsAffected = await conn.ExecuteAsync(@"
            UPDATE companies
            SET name = @name, name_ar = @nameAr, is_active = @isActive, base_currency = COALESCE(@baseCurrency, base_currency)
            WHERE id = @id;",
            new { id, name = req.Name, nameAr = req.NameAr, isActive = req.IsActive, baseCurrency = req.BaseCurrency });
        return rowsAffected == 0 ? null : await GetByIdAsync(id);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var rowsAffected = await conn.ExecuteAsync(
            "UPDATE companies SET is_active = false WHERE id = @id;",
            new { id });
        return rowsAffected > 0;
    }

    private static CompanyDto Map(CompanyRow r) => new(
        r.id, r.code, r.name, r.name_ar, r.parent_id, r.is_holding, r.base_currency, r.is_active, r.created_at);

    private record CompanyRow(
        Guid id, string code, string name, string? name_ar, Guid? parent_id,
        bool is_holding, string base_currency, bool is_active, DateTime created_at);
}
