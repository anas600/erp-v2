using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Users;

public static class UserEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization();

        grp.MapGet("/", async (UserService svc, HttpContext ctx) =>
        {
            // Only super admins can list all users
            if (!ctx.IsSuperAdmin()) return Results.Forbid();
            var data = await svc.GetAllAsync();
            return Results.Ok(data);
        });

        grp.MapGet("/{id:guid}", async (Guid id, UserService svc) =>
        {
            var u = await svc.GetByIdAsync(id);
            return u is null ? Results.NotFound() : Results.Ok(u);
        });

        grp.MapPost("/", async ([FromBody] CreateUserRequest req, UserService svc, HttpContext ctx) =>
        {
            if (!ctx.IsSuperAdmin()) return Results.Forbid();
            try
            {
                var u = await svc.CreateAsync(req);
                return Results.Created($"/api/users/{u.Id}", u);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] UpdateUserRequest req, UserService svc, HttpContext ctx) =>
        {
            if (!ctx.IsSuperAdmin()) return Results.Forbid();
            try
            {
                var u = await svc.UpdateAsync(id, req);
                return u is null ? Results.NotFound() : Results.Ok(u);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapDelete("/{id:guid}", async (Guid id, UserService svc, HttpContext ctx) =>
        {
            if (!ctx.IsSuperAdmin()) return Results.Forbid();
            var ok = await svc.DeleteAsync(id);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // Self-service: change my own password
        grp.MapPost("/me/change-password", async ([FromBody] ChangePasswordRequest req, UserService svc, HttpContext ctx) =>
        {
            var userId = ctx.GetUserId();
            if (userId is null) return Results.Unauthorized();
            try
            {
                var ok = await svc.ChangePasswordAsync(userId.Value, req.CurrentPassword, req.NewPassword);
                return ok ? Results.Ok(new { changed = true }) : Results.NotFound();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
