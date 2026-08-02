using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Auth;

public static class AuthEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/auth").WithTags("Auth");

        grp.MapPost("/login", async ([FromBody] LoginRequest req, AuthService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = new { code = "VALIDATION", message = "Email and password required" } });

            var res = await svc.LoginAsync(req.Email, req.Password);
            return res is null
                ? Results.Unauthorized()
                : Results.Ok(res);
        });

        grp.MapPost("/switch-company", async ([FromBody] SwitchCompanyRequest req, AuthService svc, HttpContext ctx) =>
        {
            var userId = ctx.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var res = await svc.SwitchCompanyAsync(userId.Value, req.CompanyId);
            return res is null
                ? Results.Forbid()
                : Results.Ok(res);
        }).RequireAuthorization();

        grp.MapGet("/me", async (HttpContext ctx, ErpV2.Common.IDbConnectionFactory db) =>
        {
            var userId = ctx.GetUserId();
            if (userId is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var user = await Dapper.SqlMapper.QuerySingleOrDefaultAsync(conn, @"
                SELECT id, email, full_name, full_name_ar, is_super_admin
                FROM users WHERE id = @id;",
                new { id = userId.Value });

            return user is null ? Results.Unauthorized() : Results.Ok(user);
        }).RequireAuthorization();
    }
}
