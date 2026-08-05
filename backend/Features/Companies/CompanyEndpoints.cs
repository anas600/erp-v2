using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Companies;

public static class CompanyEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/companies").WithTags("Companies").RequireAuthorization();

        grp.MapGet("/", async (HttpContext ctx, [FromServices] CompanyService svc) =>
        {
            var userId = ctx.GetUserId();
            if (userId is null) return Results.Unauthorized();
            var isSuper = ctx.IsSuperAdmin();
            var data = await svc.GetForUserAsync(userId.Value, isSuper);
            return Results.Ok(data);
        });

        grp.MapGet("/{id:guid}", async (Guid id, [FromServices] CompanyService svc) =>
        {
            var c = await svc.GetByIdAsync(id);
            return c is null ? Results.NotFound() : Results.Ok(c);
        });

        grp.MapPost("/", async ([FromBody] CreateCompanyRequest req, [FromServices] CompanyService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Code and Name required" });
            var c = await svc.CreateAsync(req);
            return Results.Created($"/api/companies/{c.Id}", c);
        });

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] UpdateCompanyRequest req, [FromServices] CompanyService svc) =>
        {
            var c = await svc.UpdateAsync(id, req);
            return c is null ? Results.NotFound() : Results.Ok(c);
        });

        grp.MapDelete("/{id:guid}", async (Guid id, [FromServices] CompanyService svc) =>
        {
            var ok = await svc.DeleteAsync(id);
            return ok ? Results.NoContent() : Results.NotFound();
        });
    }
}
