using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.CostCenters;

/// <summary>
/// HTTP endpoints for managing cost centers. All endpoints are
/// under /api/cost-centers and require authorization (a JWT bearer
/// token) — same as every other feature in the app.
///
/// Endpoints:
///   GET    /api/cost-centers?companyId=...  list all for a company
///   GET    /api/cost-centers/{id}          fetch one by id
///   POST   /api/cost-centers               create
///   PUT    /api/cost-centers/{id}          update (name, nameAr, isActive)
///   DELETE /api/cost-centers/{id}          soft-delete (is_active = false)
/// </summary>
public static class CostCenterEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/cost-centers").WithTags("CostCenters").RequireAuthorization();

        grp.MapGet("/", async ([FromQuery] Guid companyId, CostCenterService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var data = await svc.GetByCompanyAsync(companyId);
            return Results.Ok(data);
        });

        grp.MapGet("/{id:guid}", async (Guid id, CostCenterService svc) =>
        {
            var c = await svc.GetByIdAsync(id);
            return c is null ? Results.NotFound() : Results.Ok(c);
        });

        grp.MapPost("/", async ([FromBody] CreateCostCenterRequest req, CostCenterService svc) =>
        {
            try
            {
                if (req.CompanyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
                if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
                    return Results.BadRequest(new { error = "Code and Name required" });
                var c = await svc.CreateAsync(req);
                return Results.Created($"/api/cost-centers/{c.Id}", c);
            }
            catch (Exception ex)
            {
                // The service throws ArgumentException for validation
                // problems and InvalidOperationException for referential
                // integrity issues (e.g. project not found). Both should
                // surface as 400 with the message so the UI can show it.
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] UpdateCostCenterRequest req, CostCenterService svc) =>
        {
            try
            {
                var c = await svc.UpdateAsync(id, req);
                return c is null ? Results.NotFound() : Results.Ok(c);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapDelete("/{id:guid}", async (Guid id, CostCenterService svc) =>
        {
            try
            {
                var ok = await svc.DeleteAsync(id);
                return ok ? Results.NoContent() : Results.NotFound();
            }
            catch (Exception ex)
            {
                // "Cannot delete because posted entries reference it" is
                // a 409 Conflict semantically (the resource exists but the
                // request can't be carried out as-is), but the rest of the
                // codebase uses 400 for this pattern. Stay consistent.
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
