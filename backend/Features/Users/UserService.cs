using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Users;

/// <summary>
/// User service: manages user accounts and their company memberships.
/// Only super admins can create/delete users. Regular users can change
/// their own password via a dedicated endpoint.
/// </summary>
public class UserService
{
    private readonly IDbConnectionFactory _db;
    private readonly IPasswordHasher _hasher;

    public UserService(IDbConnectionFactory db, IPasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<List<UserDto>> GetAllAsync()
    {
        using var conn = _db.CreateConnection();
        var users = (await conn.QueryAsync<UserRow>(@"
            SELECT id, email, full_name, full_name_ar, is_super_admin, is_active, created_at
            FROM users
            ORDER BY email;")).ToList();

        var result = new List<UserDto>();
        foreach (var u in users)
        {
            var companies = (await conn.QueryAsync<UserCompanyRow>(@"
                SELECT c.id AS company_id, c.code AS company_code, c.name AS company_name, c.name_ar AS company_name_ar,
                       uc.role_id, r.name AS role_name, r.display_name_ar AS role_name_ar, uc.is_primary
                FROM user_companies uc
                JOIN companies c ON c.id = uc.company_id
                JOIN roles r ON r.id = uc.role_id
                WHERE uc.user_id = @userId
                ORDER BY uc.is_primary DESC, c.name;",
                new { userId = u.id })).ToList();

            result.Add(new UserDto(
                u.id, u.email, u.full_name, u.full_name_ar,
                u.is_super_admin, u.is_active, u.created_at,
                companies.Select(c => new UserCompanyMembership(
                    c.company_id, c.company_code, c.company_name, c.company_name_ar,
                    c.role_id, c.role_name, c.role_name_ar, c.is_primary
                )).ToList()
            ));
        }
        return result;
    }

    public async Task<UserDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var u = await conn.QuerySingleOrDefaultAsync<UserRow>(@"
            SELECT id, email, full_name, full_name_ar, is_super_admin, is_active, created_at
            FROM users WHERE id = @id;",
            new { id });
        if (u is null) return null;

        var companies = (await conn.QueryAsync<UserCompanyRow>(@"
            SELECT c.id AS company_id, c.code AS company_code, c.name AS company_name, c.name_ar AS company_name_ar,
                   uc.role_id, r.name AS role_name, r.display_name_ar AS role_name_ar, uc.is_primary
            FROM user_companies uc
            JOIN companies c ON c.id = uc.company_id
            JOIN roles r ON r.id = uc.role_id
            WHERE uc.user_id = @userId
            ORDER BY uc.is_primary DESC, c.name;",
            new { userId = u.id })).ToList();

        return new UserDto(
            u.id, u.email, u.full_name, u.full_name_ar,
            u.is_super_admin, u.is_active, u.created_at,
            companies.Select(c => new UserCompanyMembership(
                c.company_id, c.company_code, c.company_name, c.company_name_ar,
                c.role_id, c.role_name, c.role_name_ar, c.is_primary
            )).ToList()
        );
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            throw new InvalidOperationException("Email and Password required");
        if (req.Password.Length < 6)
            throw new InvalidOperationException("Password must be at least 6 characters");
        if (req.Companies.Count == 0)
            throw new InvalidOperationException("User must belong to at least one company");

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var id = Guid.NewGuid();
            await conn.ExecuteAsync(@"
                INSERT INTO users (id, email, password_hash, full_name, full_name_ar, is_super_admin, is_active)
                VALUES (@id, @email, @hash, @fullName, @fullNameAr, false, true);",
                new
                {
                    id,
                    email = req.Email.ToLowerInvariant(),
                    hash = _hasher.Hash(req.Password),
                    fullName = req.FullName,
                    fullNameAr = req.FullNameAr
                }, tx);

            foreach (var comp in req.Companies)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO user_companies (user_id, company_id, role_id, is_primary)
                    VALUES (@userId, @companyId, @roleId, @isPrimary);",
                    new
                    {
                        userId = id,
                        companyId = comp.CompanyId,
                        roleId = comp.RoleId,
                        isPrimary = comp.IsPrimary
                    }, tx);
            }

            tx.Commit();
            return (await GetByIdAsync(id))!;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<UserDto?> UpdateAsync(Guid id, UpdateUserRequest req)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var rows = await conn.ExecuteAsync(@"
                UPDATE users
                SET full_name = COALESCE(@fullName, full_name),
                    full_name_ar = COALESCE(@fullNameAr, full_name_ar),
                    is_active = COALESCE(@isActive, is_active)
                WHERE id = @id;",
                new
                {
                    id,
                    fullName = req.FullName,
                    fullNameAr = req.FullNameAr,
                    isActive = req.IsActive
                }, tx);

            if (rows == 0) return null;

            if (req.Companies is not null)
            {
                await conn.ExecuteAsync(
                    "DELETE FROM user_companies WHERE user_id = @userId;",
                    new { userId = id }, tx);

                foreach (var comp in req.Companies)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO user_companies (user_id, company_id, role_id, is_primary)
                        VALUES (@userId, @companyId, @roleId, @isPrimary);",
                        new
                        {
                            userId = id,
                            companyId = comp.CompanyId,
                            roleId = comp.RoleId,
                            isPrimary = comp.IsPrimary
                        }, tx);
                }
            }

            tx.Commit();
            return await GetByIdAsync(id);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "UPDATE users SET is_active = false WHERE id = @id;",
            new { id });
        return rows > 0;
    }

    public async Task<bool> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            throw new InvalidOperationException("كلمة المرور يجب أن تكون 6 أحرف على الأقل");

        using var conn = _db.CreateConnection();
        var hash = await conn.ExecuteScalarAsync<string?>(
            "SELECT password_hash FROM users WHERE id = @id;",
            new { id = userId });
        if (hash is null) return false;
        if (!_hasher.Verify(currentPassword, hash))
            throw new InvalidOperationException("كلمة المرور الحالية غير صحيحة");

        await conn.ExecuteAsync(
            "UPDATE users SET password_hash = @newHash WHERE id = @id;",
            new { id = userId, newHash = _hasher.Hash(newPassword) });
        return true;
    }

    private record UserRow(Guid id, string email, string? full_name, string? full_name_ar, bool is_super_admin, bool is_active, DateTime created_at);
    private record UserCompanyRow(Guid company_id, string company_code, string company_name, string? company_name_ar, Guid role_id, string role_name, string? role_name_ar, bool is_primary);
}
