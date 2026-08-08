using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 36 — Contract endpoint group.
///
/// Routes:
///   GET    /api/projects/{id}/contract  → ContractDto | 404
///   POST   /api/projects/{id}/contract  → ContractDto (one per project)
///   PUT    /api/contracts/{id}          → ContractDto | 404
///   DELETE /api/contracts/{id}          → 204 | 404
///
/// All routes require auth (RequireAuthorization). The contract is
/// uniquely keyed by (company_id, project_id) — see migration 021.
/// </summary>
public static class ContractEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api").WithTags("Contracts").RequireAuthorization();

        // GET /api/projects/{id}/contract — returns the contract for
        // a project, or 404 if the project has none yet.
        grp.MapGet("/projects/{id:guid}/contract", async (
            Guid id, [FromServices] ContractService svc) =>
        {
            var contract = await svc.GetByProjectAsync(id);
            return contract is null ? Results.NotFound() : Results.Ok(contract);
        });

        // POST /api/projects/{id}/contract — creates a contract for
        // the project. Refuses if the project already has one.
        grp.MapPost("/projects/{id:guid}/contract", async (
            Guid id,
            [FromBody] CreateContractRequest req,
            [FromServices] ContractService svc) =>
        {
            try
            {
                if (req is null)
                    return Results.BadRequest(new { error = "request body required" });
                var contract = await svc.CreateAsync(id, req);
                return Results.Created($"/api/contracts/{contract.Id}", contract);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // PUT /api/contracts/{id} — replaces the contract's terms.
        grp.MapPut("/contracts/{id:guid}", async (
            Guid id,
            [FromBody] UpdateContractRequest req,
            [FromServices] ContractService svc) =>
        {
            try
            {
                if (req is null)
                    return Results.BadRequest(new { error = "request body required" });
                var contract = await svc.UpdateAsync(id, req);
                return contract is null ? Results.NotFound() : Results.Ok(contract);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // DELETE /api/contracts/{id} — cascades to billings via
        // the FK ON DELETE CASCADE.
        grp.MapDelete("/contracts/{id:guid}", async (
            Guid id, [FromServices] ContractService svc) =>
        {
            var ok = await svc.DeleteAsync(id);
            return ok ? Results.NoContent() : Results.NotFound();
        });
    }
}

/// <summary>
/// Sprint 36 — Progress billing endpoint group (المستخلصات).
///
/// Routes:
///   GET   /api/projects/{id}/billings    → List&lt;ProgressBillingDto&gt;
///   POST  /api/projects/{id}/billings    → ProgressBillingDto (DRAFT)
///   GET   /api/billings/{id}             → ProgressBillingDto
///   PUT   /api/billings/{id}             → ProgressBillingDto (DRAFT only)
///   POST  /api/billings/{id}/approve     → ProgressBillingDto (INVOICED)
///   POST  /api/billings/{id}/cancel      → ProgressBillingDto (CANCELLED)
///   GET   /api/projects/{id}/wip         → WipResponse
///   GET   /api/projects/{id}/statement   → ClientStatementResponse
/// </summary>
public static class BillingEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api").WithTags("Billings").RequireAuthorization();

        // GET /api/projects/{id}/billings — all billings for a project.
        grp.MapGet("/projects/{id:guid}/billings", async (
            Guid id, [FromServices] BillingService svc) =>
        {
            var list = await svc.GetByProjectAsync(id);
            return Results.Ok(list);
        });

        // POST /api/projects/{id}/billings — create a new DRAFT billing.
        grp.MapPost("/projects/{id:guid}/billings", async (
            Guid id,
            [FromBody] CreateBillingRequest req,
            [FromServices] BillingService svc) =>
        {
            try
            {
                if (req is null)
                    return Results.BadRequest(new { error = "request body required" });
                var billing = await svc.CreateAsync(id, req);
                return Results.Created($"/api/billings/{billing.Id}", billing);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // GET /api/billings/{id}
        grp.MapGet("/billings/{id:guid}", async (
            Guid id, [FromServices] BillingService svc) =>
        {
            var b = await svc.GetByIdAsync(id);
            return b is null ? Results.NotFound() : Results.Ok(b);
        });

        // PUT /api/billings/{id} — only allowed on DRAFT.
        grp.MapPut("/billings/{id:guid}", async (
            Guid id,
            [FromBody] UpdateBillingRequest req,
            [FromServices] BillingService svc) =>
        {
            try
            {
                if (req is null)
                    return Results.BadRequest(new { error = "request body required" });
                var b = await svc.UpdateAsync(id, req);
                return b is null ? Results.NotFound() : Results.Ok(b);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/billings/{id}/approve — DRAFT → INVOICED (atomic).
        grp.MapPost("/billings/{id:guid}/approve", async (
            Guid id,
            [FromBody] ApproveBillingRequest req,
            [FromServices] BillingService svc) =>
        {
            try
            {
                // If the caller didn't send a body, default to the
                // billing's own date and no extra notes.
                var effectiveReq = req ?? new ApproveBillingRequest(
                    BillingDate: DateTime.UtcNow, Notes: null);
                var b = await svc.ApproveAsync(id, effectiveReq);
                return Results.Ok(b);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/billings/{id}/cancel — DRAFT → CANCELLED.
        grp.MapPost("/billings/{id:guid}/cancel", async (
            Guid id, [FromServices] BillingService svc) =>
        {
            try
            {
                var b = await svc.CancelAsync(id);
                return Results.Ok(b);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // GET /api/projects/{id}/wip — Work-in-Progress snapshot.
        grp.MapGet("/projects/{id:guid}/wip", async (
            Guid id, [FromServices] BillingService svc) =>
        {
            var wip = await svc.GetWipAsync(id);
            return wip is null ? Results.NotFound() : Results.Ok(wip);
        });

        // GET /api/projects/{id}/statement — client statement.
        grp.MapGet("/projects/{id:guid}/statement", async (
            Guid id, [FromServices] BillingService svc) =>
        {
            var stmt = await svc.GetStatementAsync(id);
            return stmt is null ? Results.NotFound() : Results.Ok(stmt);
        });

        // GET /api/billings/{id}/line-items — Sprint 38 BOQ view.
        // Returns the line items claimed on this billing (the
        // billing_line_items table).
        grp.MapGet("/billings/{id:guid}/line-items", async (
            Guid id, [FromServices] BillingService svc) =>
        {
            var list = await svc.GetBillingLineItemsAsync(id);
            return Results.Ok(list);
        });

        // POST /api/billings/{id}/preview-line-items — Sprint 38 live preview.
        // Body: { items: [{lineItemId, quantityThisPeriod}, ...] }
        // Returns the preview rows so the UI can show "what the
        // amounts WOULD be" before the user commits the billing.
        grp.MapPost("/billings/{id:guid}/preview-line-items", async (
            Guid id,
            [FromBody] PreviewBillingLineItemsRequest? body,
            [FromServices] BillingService svc,
            [FromServices] ContractService contracts) =>
        {
            try
            {
                var billing = await svc.GetByIdAsync(id);
                if (billing is null)
                    return Results.NotFound();
                // We need the contractId to validate items; the
                // preview doesn't write, so this is read-only.
                var items = body?.Items ?? new List<CreateBillingLineItemRequest>();
                var previews = await svc.PreviewBillingLineItemsAsync(
                    billing.ContractId, items);
                return Results.Ok(previews);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

/// <summary>
/// Body for POST /api/billings/{id}/preview-line-items. Just the
/// list of (lineItemId, quantityThisPeriod) pairs the UI is
/// considering — no commit, no side effects.
/// </summary>
public record PreviewBillingLineItemsRequest(
    List<CreateBillingLineItemRequest> Items
);
