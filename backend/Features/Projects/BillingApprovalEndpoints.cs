using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 57 — 4-party billing approval endpoints.
///
/// All endpoints require super_admin. In a production deployment
/// you'd have role-based checks (e.g. only the consultant user
/// can approve as 'consultant'), but for the demo we use the
/// super_admin gate consistently with other admin actions.
/// </summary>
public static class BillingApprovalEndpoints
{
    public static void MapBillingApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api").WithTags("BillingApprovals");

        // GET the 4 approval rows for a billing (lazy-creates them)
        grp.MapGet("/projects/{projectId:guid}/billings/{billingId:guid}/approvals", async (
            Guid projectId, Guid billingId,
            [FromServices] BillingApprovalService svc,
            HttpContext ctx) =>
        {
            if (!ctx.IsSuperAdmin())
                return Results.Json(
                    new { error = "يتطلب صلاحية المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            try
            {
                var list = await svc.ListAsync(billingId);
                return Results.Ok(list);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST: approve a specific role
        grp.MapPost("/projects/{projectId:guid}/billings/{billingId:guid}/approvals/{role}/approve", async (
            Guid projectId, Guid billingId, string role,
            [FromBody] ApproveBillingApprovalRequest? req,
            [FromServices] BillingApprovalService svc,
            HttpContext ctx) =>
        {
            if (!ctx.IsSuperAdmin())
                return Results.Json(
                    new { error = "يتطلب صلاحية المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            try
            {
                var result = await svc.ApproveAsync(billingId, role, req?.Notes);
                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST: reject a specific role
        grp.MapPost("/projects/{projectId:guid}/billings/{billingId:guid}/approvals/{role}/reject", async (
            Guid projectId, Guid billingId, string role,
            [FromBody] RejectBillingApprovalRequest req,
            [FromServices] BillingApprovalService svc,
            HttpContext ctx) =>
        {
            if (!ctx.IsSuperAdmin())
                return Results.Json(
                    new { error = "يتطلب صلاحية المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            try
            {
                var result = await svc.RejectAsync(billingId, role, req.Reason, req.Notes);
                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // DELETE: reset a role back to pending
        grp.MapDelete("/projects/{projectId:guid}/billings/{billingId:guid}/approvals/{role}", async (
            Guid projectId, Guid billingId, string role,
            [FromServices] BillingApprovalService svc,
            HttpContext ctx) =>
        {
            if (!ctx.IsSuperAdmin())
                return Results.Json(
                    new { error = "يتطلب صلاحية المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            try
            {
                var result = await svc.ResetAsync(billingId, role);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
