using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ErpV2.Common;

/// <summary>
/// JWT token service. Generates tokens with user claims + company context.
/// </summary>
public class JwtTokenService
{
    private readonly string _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expiryMinutes;

    public JwtTokenService(IConfiguration config)
    {
        _key = config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key missing");
        _issuer = config["Jwt:Issuer"] ?? "erp-v2";
        _audience = config["Jwt:Audience"] ?? "erp-v2-client";
        _expiryMinutes = int.TryParse(config["Jwt:ExpiryMinutes"], out var m) ? m : 1440;
    }

    public string GenerateToken(Guid userId, string email, bool isSuperAdmin, IEnumerable<Guid> companyIds, Guid? activeCompanyId, IEnumerable<string> roles, IEnumerable<string> permissions)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new("is_super_admin", isSuperAdmin.ToString().ToLowerInvariant()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        foreach (var cid in companyIds)
            claims.Add(new Claim("company_id", cid.ToString()));

        if (activeCompanyId.HasValue)
            claims.Add(new Claim("active_company_id", activeCompanyId.Value.ToString()));

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        foreach (var perm in permissions)
            claims.Add(new Claim("permission", perm));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_expiryMinutes),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken() => Convert.ToBase64String(Guid.NewGuid().ToByteArray());
}

/// <summary>
/// Helper to extract the active company id from the current HTTP context.
/// </summary>
public static class CurrentContext
{
    public static Guid? GetActiveCompanyId(this HttpContext ctx)
    {
        var claim = ctx.User.FindFirst("active_company_id")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    public static Guid? GetActiveCompanyIdFromHeader(this HttpContext ctx)
    {
        // Fall back to X-Company-Id header if active_company_id not in token
        if (ctx.Request.Headers.TryGetValue("X-Company-Id", out var values))
        {
            var raw = values.ToString();
            if (Guid.TryParse(raw, out var id)) return id;
        }
        return ctx.GetActiveCompanyId();
    }

    public static Guid? GetUserId(this HttpContext ctx)
    {
        var sub = ctx.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                 ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    public static bool IsSuperAdmin(this HttpContext ctx)
    {
        var v = ctx.User.FindFirst("is_super_admin")?.Value;
        return bool.TryParse(v, out var b) && b;
    }
}
