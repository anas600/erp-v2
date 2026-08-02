using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Journal;

public static class JournalEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/journal").WithTags("Journal").RequireAuthorization();

        grp.MapGet("/", async ([FromQuery] Guid companyId, [FromQuery] int? limit, JournalService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var data = await svc.GetByCompanyAsync(companyId, limit ?? 50);
            return Results.Ok(data);
        });

        grp.MapGet("/{id:guid}", async (Guid id, JournalService svc) =>
        {
            var entry = await svc.GetByIdAsync(id);
            return entry is null ? Results.NotFound() : Results.Ok(entry);
        });

        grp.MapPost("/", async ([FromBody] CreateJournalEntryRequest req, JournalService svc, HttpContext ctx) =>
        {
            try
            {
                var userId = ctx.GetUserId();
                var entry = await svc.CreateDraftAsync(req, userId);
                return Results.Created($"/api/journal/{entry.Id}", entry);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPost("/{id:guid}/post", async (Guid id, JournalService svc) =>
        {
            try
            {
                var entry = await svc.PostAsync(id);
                return Results.Ok(entry);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPost("/{id:guid}/reverse", async (Guid id, JournalService svc) =>
        {
            try
            {
                var ok = await svc.ReverseAsync(id);
                return ok ? Results.NoContent() : Results.NotFound();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
