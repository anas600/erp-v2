using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Auth;

public class AuthService
{
    private readonly IDbConnectionFactory _db;
    private readonly IPasswordHasher _hasher;
    private readonly JwtTokenService _jwt;

    public AuthService(IDbConnectionFactory db, IPasswordHasher hasher, JwtTokenService jwt)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
    }

    /// <summary>
    /// Authenticates a user by email and password and returns a fresh JWT.
    /// Returns null if the user does not exist, is inactive, or the password is wrong.
    ///
    /// The returned token includes the union of all permissions across the user's roles,
    /// the list of company ids the user can access, and the primary company (or the
    /// first one in the list if no primary is set) as the active company.
    /// </summary>
    public async Task<LoginResponse?> LoginAsync(string email, string password)
    {
        using var conn = _db.CreateConnection();

        var user = await conn.QuerySingleOrDefaultAsync<UserRow>(@"
            SELECT id, email, password_hash, full_name, full_name_ar, is_super_admin
            FROM users
            WHERE email = @email AND is_active = true;",
            new { email });

        if (user is null) return null;
        if (!_hasher.Verify(password, user.password_hash)) return null;

        var companies = (await conn.QueryAsync<CompanyRow>(@"
            SELECT c.id, c.code, c.name, c.name_ar, uc.role_id, r.name AS role_name, uc.is_primary
            FROM user_companies uc
            JOIN companies c ON c.id = uc.company_id
            JOIN roles r ON r.id = uc.role_id
            WHERE uc.user_id = @userId
            ORDER BY uc.is_primary DESC, c.name;",
            new { userId = user.id })).ToList();

        var companyIds = companies.Select(c => c.id).ToList();
        var activeCompanyId = companies.FirstOrDefault(c => c.is_primary)?.id ?? companies.FirstOrDefault()?.id;

        // Get all permissions for user's roles
        var permissions = (await conn.QueryAsync<string>(@"
            SELECT DISTINCT p.code
            FROM role_permissions rp
            JOIN permissions p ON p.id = rp.permission_id
            WHERE rp.role_id = ANY(@roleIds);",
            new { roleIds = companies.Select(c => c.role_id).Distinct().ToArray() })).ToList();

        var roleNames = companies.Select(c => c.role_name).Distinct().ToList();

        var token = _jwt.GenerateToken(
            user.id, user.email, user.is_super_admin,
            companyIds, activeCompanyId, roleNames, permissions);

        return new LoginResponse(
            token,
            _jwt.GenerateRefreshToken(),
            new UserInfo(user.id, user.email, user.full_name, user.full_name_ar, user.is_super_admin),
            companies.Select(c => new CompanyInfo(
                c.id, c.code, c.name, c.name_ar, c.role_id, c.role_name, c.is_primary)).ToList()
        );
    }

    /// <summary>
    /// Issues a new JWT with a different active company. The user's company list and
    /// permissions stay the same; only `active_company_id` changes.
    /// Returns null if the user is missing or the requested company is not in their list.
    /// </summary>
    public async Task<LoginResponse?> SwitchCompanyAsync(Guid userId, Guid newCompanyId)
    {
        using var conn = _db.CreateConnection();

        var user = await conn.QuerySingleOrDefaultAsync<UserRow>(@"
            SELECT id, email, password_hash, full_name, full_name_ar, is_super_admin
            FROM users
            WHERE id = @userId AND is_active = true;",
            new { userId });

        if (user is null) return null;

        var companies = (await conn.QueryAsync<CompanyRow>(@"
            SELECT c.id, c.code, c.name, c.name_ar, uc.role_id, r.name AS role_name, uc.is_primary
            FROM user_companies uc
            JOIN companies c ON c.id = uc.company_id
            JOIN roles r ON r.id = uc.role_id
            WHERE uc.user_id = @userId
            ORDER BY uc.is_primary DESC, c.name;",
            new { userId })).ToList();

        if (!companies.Any(c => c.id == newCompanyId)) return null;

        var companyIds = companies.Select(c => c.id).ToList();
        var permissions = (await conn.QueryAsync<string>(@"
            SELECT DISTINCT p.code
            FROM role_permissions rp
            JOIN permissions p ON p.id = rp.permission_id
            WHERE rp.role_id = ANY(@roleIds);",
            new { roleIds = companies.Select(c => c.role_id).Distinct().ToArray() })).ToList();

        var roleNames = companies.Select(c => c.role_name).Distinct().ToList();

        var token = _jwt.GenerateToken(
            user.id, user.email, user.is_super_admin,
            companyIds, newCompanyId, roleNames, permissions);

        return new LoginResponse(
            token,
            _jwt.GenerateRefreshToken(),
            new UserInfo(user.id, user.email, user.full_name, user.full_name_ar, user.is_super_admin),
            companies.Select(c => new CompanyInfo(
                c.id, c.code, c.name, c.name_ar, c.role_id, c.role_name, c.is_primary)).ToList()
        );
    }

    private record UserRow(Guid id, string email, string password_hash, string? full_name, string? full_name_ar, bool is_super_admin);
    private record CompanyRow(Guid id, string code, string name, string? name_ar, Guid role_id, string role_name, bool is_primary);
}
