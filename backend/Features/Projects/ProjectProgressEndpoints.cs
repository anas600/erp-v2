using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 56 — Project Technical Report endpoints.
/// </summary>
public static class ProjectProgressEndpoints
{
    public static void MapProjectProgressEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api").WithTags("ProjectProgress");

        // Get full progress (header + per-line items)
        grp.MapGet("/projects/{id:guid}/progress", async (
            Guid id, [FromServices] ProjectProgressService svc) =>
        {
            try
            {
                var p = await svc.GetProgressAsync(id);
                return Results.Ok(p);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Update header (4 status flags + 2 progress percents)
        grp.MapPatch("/projects/{id:guid}/progress", async (
            Guid id,
            [FromBody] UpdateProjectProgressRequest req,
            [FromServices] ProjectProgressService svc) =>
        {
            try
            {
                var p = await svc.UpdateHeaderAsync(id, req);
                return Results.Ok(p);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Update a single line item's progress
        grp.MapPatch("/projects/{projectId:guid}/line-items/{lineItemId:guid}/progress", async (
            Guid projectId, Guid lineItemId,
            [FromBody] UpdateLineItemProgressRequest req,
            [FromServices] ProjectProgressService svc) =>
        {
            try
            {
                var p = await svc.UpdateLineItemProgressAsync(projectId, lineItemId, req);
                return Results.Ok(p);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
