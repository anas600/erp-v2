using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Journal;

public static class JournalEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/journal").WithTags("Journal").RequireAuthorization();

        grp.MapGet("/", async ([FromQuery] Guid companyId, [FromQuery] int? limit, [FromQuery] int? offset, [FromQuery] string? status, [FromServices] JournalService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });

            // Sprint 41 — pagination. The frontend passes
            // ?limit=50&offset=0 (or ?offset=50, 100, ...) to step
            // through pages. We return {items, total, limit, offset}
            // so the page navigator can show "page N of M" and the
            // total count.
            //
            // Backwards compat: if no offset is passed, fall back to
            // the legacy flat-list response so any older client
            // (e.g. direct Swagger calls) keeps working.
            if (offset.HasValue || status is not null)
            {
                var pageSize = limit ?? 50;
                var off = offset ?? 0;
                var (items, total) = await svc.GetByCompanyPagedAsync(companyId, pageSize, off, status);
                return Results.Ok(new
                {
                    items,
                    total,
                    limit = pageSize,
                    offset = off
                });
            }

            var data = await svc.GetByCompanyAsync(companyId, limit ?? 50);
            return Results.Ok(data);
        });

        // Sprint 41 — bulk approve every PENDING entry in a
        // company. Returns a per-company summary suitable for the
        // frontend's "موافقة الكل" button or admin recovery.
        grp.MapPost("/bulk-approve", async (HttpContext ctx, [FromServices] JournalService svc, [FromQuery] Guid companyId) =>
        {
            if (companyId == Guid.Empty)
                return Results.BadRequest(new { error = "companyId required" });
            var userId = ctx.GetUserId();
            var (succeeded, failures) = await svc.BulkApproveByCompanyAsync(companyId, userId);
            return Results.Ok(new
            {
                approved = succeeded.Count,
                failed = failures.Count,
                succeededIds = succeeded,
                failures = failures.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
            });
        });

        // Sprint 41 — bulk post every DRAFT or PENDING entry in a
        // company. Same shape as bulk-approve.
        grp.MapPost("/bulk-post", async ([FromServices] JournalService svc, [FromQuery] Guid companyId) =>
        {
            if (companyId == Guid.Empty)
                return Results.BadRequest(new { error = "companyId required" });
            var (succeeded, failures) = await svc.BulkPostByCompanyAsync(companyId);
            return Results.Ok(new
            {
                posted = succeeded.Count,
                failed = failures.Count,
                succeededIds = succeeded,
                failures = failures.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
            });
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

        // Sprint 59 — Update a DRAFT journal entry. Only entries in
        // 'draft' state are editable (manual drafts, or rejected rule
        // entries that landed in 'draft' via /reject). Posted /
        // reversed entries are immutable — they belong to the
        // permanent accounting record and must be undone via /reverse.
        //
        // Returns:
        //   200 OK   — entry updated
        //   404 NF   — entry didn't exist
        //   400 Bad  — entry exists but is in a non-draft state, or
        //              the new request fails validation (period lock,
        //              unbalanced, etc.)
        grp.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] CreateJournalEntryRequest req,
            [FromServices] JournalService svc,
            HttpContext ctx) =>
        {
            try
            {
                var userId = ctx.GetUserId();
                var entry = await svc.UpdateDraftAsync(id, req, userId);
                return entry is null ? Results.NotFound() : Results.Ok(entry);
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
