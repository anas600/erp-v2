using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Projects;

public static class ProjectEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/projects").WithTags("Projects").RequireAuthorization();

        grp.MapGet("/", async ([FromQuery] Guid companyId, [FromServices] ProjectService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var data = await svc.GetByCompanyAsync(companyId);
            return Results.Ok(data);
        });

        grp.MapGet("/{id:guid}", async (Guid id, [FromServices] ProjectService svc) =>
        {
            var p = await svc.GetByIdAsync(id);
            return p is null ? Results.NotFound() : Results.Ok(p);
        });

        // Sprint 50 — Returns the L4 sub-ledger accounts that were
        // auto-created for this project's cost tracking (e.g. the
        // 5401-PRJ-XXX, 5402-PRJ-XXX, ..., 5407-PRJ-XXX rows). The
        // frontend's invoice form uses this to populate the "line
        // account" dropdown when the user picks a project.
        grp.MapGet("/{id:guid}/cost-accounts", async (
            Guid id, [FromServices] ProjectCostAccountService svc) =>
        {
            var accounts = await svc.GetSubLedgersForProjectAsync(id);
            return Results.Ok(accounts);
        });

        grp.MapPost("/", async ([FromBody] CreateProjectRequest req, [FromServices] ProjectService svc) =>
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

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] UpdateProjectRequest req, [FromServices] ProjectService svc) =>
        {
            try
            {
                var p = await svc.UpdateAsync(id, req);
                return p is null ? Results.NotFound() : Results.Ok(p);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapDelete("/{id:guid}", async (Guid id, [FromServices] ProjectService svc) =>
        {
            var ok = await svc.DeleteAsync(id);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // Milestones
        grp.MapPost("/{projectId:guid}/milestones", async (
            Guid projectId,
            [FromBody] CreateMilestoneRequest req,
            [FromServices] ProjectService svc) =>
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
            [FromServices] ProjectService svc, HttpContext ctx) =>
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

        // ============================================================
        // Sprint 35 — Cost allocation endpoints (bulk)
        // ============================================================

        /// <summary>
        /// Bulk-assign the given invoices to this project. Idempotent.
        /// Returns 400 if any invoice belongs to a different company
        /// than the project. The body is a list of invoice GUIDs.
        /// </summary>
        grp.MapPost("/{id:guid}/allocate-invoices", async (
            Guid id,
            [FromBody] AllocateRequest req,
            [FromServices] ProjectService svc) =>
        {
            try
            {
                var rows = await svc.AllocateInvoicesAsync(id, req?.InvoiceIds ?? new List<Guid>());
                return Results.Ok(new { allocated = rows });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        /// <summary>
        /// Bulk-assign the given journal entries to this project.
        /// Idempotent. Cross-company safe (returns 400 on mismatch).
        /// </summary>
        grp.MapPost("/{id:guid}/allocate-journal-entries", async (
            Guid id,
            [FromBody] AllocateJournalEntriesRequest req,
            [FromServices] ProjectService svc) =>
        {
            try
            {
                var rows = await svc.AllocateJournalEntriesAsync(id, req?.JournalEntryIds ?? new List<Guid>());
                return Results.Ok(new { allocated = rows });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        /// <summary>
        /// Bulk-deallocate (clear project_id on) the given invoices.
        /// Idempotent — deallocating an invoice that isn't tagged
        /// with this project is a no-op.
        /// </summary>
        grp.MapPost("/{id:guid}/deallocate-invoices", async (
            Guid id,
            [FromBody] AllocateRequest req,
            [FromServices] ProjectService svc) =>
        {
            try
            {
                var rows = await svc.DeallocateInvoicesAsync(id, req?.InvoiceIds ?? new List<Guid>());
                return Results.Ok(new { deallocated = rows });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ============================================================
        // Sprint 35 — P&L + cost/revenue drill-down endpoints
        // ============================================================

        /// <summary>
        /// Per-project P&amp;L report: revenue (posted sales invoices)
        /// minus costs (journal lines on accounts 5401-5407), grouped
        /// by account code with a margin percent.
        /// </summary>
        grp.MapGet("/{id:guid}/pnl", async (Guid id, [FromServices] ProjectService svc) =>
        {
            var pnl = await svc.GetPnLAsync(id);
            return pnl is null ? Results.NotFound() : Results.Ok(pnl);
        });

        /// <summary>
        /// All costs (invoices + journal lines) tagged with this project.
        /// Used by the "Project Costs" page.
        /// </summary>
        grp.MapGet("/{id:guid}/costs", async (Guid id, [FromServices] ProjectService svc) =>
        {
            var costs = await svc.GetCostsAsync(id);
            return Results.Ok(costs);
        });

        /// <summary>
        /// All sales invoices tagged with this project (the "revenue"
        /// side of P&amp;L).
        /// </summary>
        grp.MapGet("/{id:guid}/revenue", async (Guid id, [FromServices] ProjectService svc) =>
        {
            var revenue = await svc.GetRevenueAsync(id);
            return Results.Ok(revenue);
        });
    }
}

/// <summary>
/// Sprint 35 — company-wide P&amp;L report endpoint group. Lives at
/// /api/reports/projects-pnl?companyId=... so it groups with the
/// other reports (no need to re-route the existing
/// ReportEndpoints). Single endpoint, returns List&lt;ProjectPnLResponse&gt;
/// for every project in the company.
/// </summary>
public static class ProjectPnLReportEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/reports").WithTags("Reports").RequireAuthorization();

        grp.MapGet("/projects-pnl", async (
            [FromQuery] Guid companyId,
            [FromServices] ProjectService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var report = await svc.GetCompanyPnLAsync(companyId);
            return Results.Ok(report);
        });
    }
}
