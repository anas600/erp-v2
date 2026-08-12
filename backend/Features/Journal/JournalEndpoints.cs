using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Journal;

public static class JournalEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/journal").WithTags("Journal").RequireAuthorization();

        grp.MapGet("/", async (
            [FromQuery] Guid companyId,
            [FromQuery] int? limit,
            [FromQuery] int? offset,
            [FromQuery] string? status,
            [FromServices] JournalService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var pageSize = Math.Clamp(limit ?? 50, 1, 500);
            var skip = Math.Max(offset ?? 0, 0);
            var data = await svc.GetByCompanyPagedAsync(companyId, pageSize, skip, status);
            var total = await svc.CountByCompanyAsync(companyId, status);
            return Results.Ok(new { items = data, total, limit = pageSize, offset = skip });
        });

        grp.MapGet("/{id:guid}", async (Guid id, [FromServices] JournalService svc) =>
        {
            var entry = await svc.GetByIdAsync(id);
            return entry is null ? Results.NotFound() : Results.Ok(entry);
        });

        grp.MapPost("/", async ([FromBody] CreateJournalEntryRequest req, [FromServices] JournalService svc, HttpContext ctx) =>
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

        grp.MapPost("/{id:guid}/post", async (Guid id, [FromServices] JournalService svc) =>
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

        // Sprint 41 — Bulk operations. The accountant reviews the
        // Journal page, filters by status (e.g. "draft"), and clicks
        // "موافقة الكل" / "ترحيل الكل" to push every matching entry
        // through the gate in one shot. Failures are reported per
        // entry so the UI can show which ones still need attention.
        grp.MapPost("/bulk-approve", async (
            [FromQuery] Guid companyId,
            [FromServices] JournalService svc,
            HttpContext ctx) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var userId = ctx.GetUserId();
            var (succeeded, failed) = await svc.BulkApproveByCompanyAsync(companyId, userId);
            return Results.Ok(new { approved = succeeded.Count, failed = failed.Count, succeededIds = succeeded, failures = failed });
        });

        grp.MapPost("/bulk-post", async (
            [FromQuery] Guid companyId,
            [FromServices] JournalService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var (succeeded, failed) = await svc.BulkPostByCompanyAsync(companyId);
            return Results.Ok(new { posted = succeeded.Count, failed = failed.Count, succeededIds = succeeded, failures = failed });
        });

        grp.MapPost("/{id:guid}/reverse", async (Guid id, [FromServices] JournalService svc) =>
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

        // Sprint 18 — Delete a DRAFT journal entry (and its lines).
        // Posted/Pending/Reversed entries are protected — they are part
        // of the permanent accounting record. Use the /reverse endpoint
        // for posted entries, /reject for pending ones.
        //
        // Returns:
        //   200 OK   — entry deleted
        //   404 NF   — entry didn't exist
        //   400 Bad  — entry exists but is in a non-draft state
        grp.MapDelete("/{id:guid}", async (Guid id, [FromServices] JournalService svc) =>
        {
            try
            {
                var ok = await svc.DeleteAsync(id);
                return ok ? Results.Ok(new { deleted = true }) : Results.NotFound();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Sprint 15 — Approve a pending entry (rule-generated).
        // Transitions: pending → posted. Affects financial reports on success.
        grp.MapPost("/{id:guid}/approve", async (Guid id, [FromServices] JournalService svc, HttpContext ctx) =>
        {
            try
            {
                var userId = ctx.GetUserId();
                var entry = await svc.ApproveAsync(id, userId);
                return entry is null ? Results.NotFound() : Results.Ok(entry);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Sprint 15 — Reject a pending entry.
        // Transitions: pending → draft. The entry keeps the rule's
        // source/reference so the rule author can investigate.
        grp.MapPost("/{id:guid}/reject", async (Guid id, [FromBody] RejectRequest? req, [FromServices] JournalService svc, HttpContext ctx) =>
        {
            try
            {
                var userId = ctx.GetUserId();
                var entry = await svc.RejectAsync(id, userId, req?.Reason);
                return entry is null ? Results.NotFound() : Results.Ok(entry);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Sprint 15 — List pending entries for a company (for the
        // "Pending Entries" page on the accountant's dashboard).
        grp.MapGet("/pending", async ([FromQuery] Guid companyId, [FromServices] JournalService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var data = await svc.GetPendingAsync(companyId);
            return Results.Ok(data);
        });
    }

    public record RejectRequest(string? Reason);
}
