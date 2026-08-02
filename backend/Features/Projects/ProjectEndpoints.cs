using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Projects;

public static class ProjectEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/projects").WithTags("Projects").RequireAuthorization();

        grp.MapGet("/", async ([FromQuery] Guid companyId, ProjectService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var data = await svc.GetByCompanyAsync(companyId);
            return Results.Ok(data);
        });

        grp.MapGet("/{id:guid}", async (Guid id, ProjectService svc) =>
        {
            var p = await svc.GetByIdAsync(id);
            return p is null ? Results.NotFound() : Results.Ok(p);
        });

        grp.MapPost("/", async ([FromBody] CreateProjectRequest req, ProjectService svc) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
                    return Results.BadRequest(new { error = "Code and Name required" });
                var p = await svc.CreateAsync(req);
                return Results.Created($"/api/projects/{p.Id}", p);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] UpdateProjectRequest req, ProjectService svc) =>
        {
            var p = await svc.UpdateAsync(id, req);
            return p is null ? Results.NotFound() : Results.Ok(p);
        });

        grp.MapDelete("/{id:guid}", async (Guid id, ProjectService svc) =>
        {
            var ok = await svc.DeleteAsync(id);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // Milestones
        grp.MapPost("/{projectId:guid}/milestones", async (
            Guid projectId,
            [FromBody] CreateMilestoneRequest req,
            ProjectService svc) =>
        {
            try
            {
                var m = await svc.AddMilestoneAsync(projectId, req);
                return Results.Created($"/api/projects/{projectId}/milestones/{m.Id}", m);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPost("/{projectId:guid}/milestones/{milestoneId:guid}/complete", async (
            Guid projectId, Guid milestoneId,
            ProjectService svc, HttpContext ctx) =>
        {
            try
            {
                var userId = ctx.GetUserId();
                var entries = await svc.CompleteMilestoneAsync(projectId, milestoneId, userId);
                return Results.Ok(new { completed = true, journalEntriesCreated = entries.Count });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
