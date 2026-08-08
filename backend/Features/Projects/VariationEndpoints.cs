using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 38 — Contract Variation endpoint group.
///
/// Routes:
///   GET  /api/contracts/{id}/variations         → List&lt;ContractVariationDto&gt;
///   POST /api/contracts/{id}/variations         → ContractVariationDto (DRAFT)
///   GET  /api/variations/{id}                   → ContractVariationDto
///   POST /api/variations/{id}/items             → ContractVariationItemDto
///   PUT  /api/variation-items/{id}              → ContractVariationItemDto
///   DELETE /api/variation-items/{id}            → 204 | 404
///   POST /api/variations/{id}/approve           → ContractVariationDto (APPROVED)
///   POST /api/variations/{id}/reject            → ContractVariationDto (REJECTED)
///   GET  /api/contracts/{id}/effective-value    → EffectiveContractValueResponse
///
/// All routes require auth.
/// </summary>
public static class VariationEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api").WithTags("Variations").RequireAuthorization();

        // GET /api/contracts/{id}/variations
        grp.MapGet("/contracts/{contractId:guid}/variations", async (
            Guid contractId, [FromServices] VariationService svc) =>
        {
            var list = await svc.GetByContractAsync(contractId);
            return Results.Ok(list);
        });

        // POST /api/contracts/{id}/variations
        grp.MapPost("/contracts/{contractId:guid}/variations", async (
            Guid contractId,
            [FromBody] CreateVariationRequest req,
            [FromServices] VariationService svc) =>
        {
            try
            {
                if (req is null)
                    return Results.BadRequest(new { error = "request body required" });
                var v = await svc.CreateAsync(contractId, req);
                return Results.Created($"/api/variations/{v.Id}", v);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // GET /api/variations/{id}
        grp.MapGet("/variations/{id:guid}", async (
            Guid id, [FromServices] VariationService svc) =>
        {
            var v = await svc.GetByIdAsync(id);
            return v is null ? Results.NotFound() : Results.Ok(v);
        });

        // POST /api/variations/{id}/items
        grp.MapPost("/variations/{variationId:guid}/items", async (
            Guid variationId,
            [FromBody] AddVariationItemRequest req,
            [FromServices] VariationService svc) =>
        {
            try
            {
                if (req is null)
                    return Results.BadRequest(new { error = "request body required" });
                var item = await svc.AddItemAsync(variationId, req);
                return Results.Created($"/api/variation-items/{item.Id}", item);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // PUT /api/variation-items/{id}
        grp.MapPut("/variation-items/{id:guid}", async (
            Guid id,
            [FromBody] UpdateVariationItemRequest req,
            [FromServices] VariationService svc) =>
        {
            try
            {
                if (req is null)
                    return Results.BadRequest(new { error = "request body required" });
                var item = await svc.UpdateItemAsync(id, req);
                return item is null ? Results.NotFound() : Results.Ok(item);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // DELETE /api/variation-items/{id}
        grp.MapDelete("/variation-items/{id:guid}", async (
            Guid id, [FromServices] VariationService svc) =>
        {
            try
            {
                var ok = await svc.RemoveItemAsync(id);
                return ok ? Results.NoContent() : Results.NotFound();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/variations/{id}/approve
        grp.MapPost("/variations/{id:guid}/approve", async (
            Guid id,
            [FromBody] ApproveVariationRequest? req,
            HttpContext ctx,
            [FromServices] VariationService svc) =>
        {
            try
            {
                var userId = ctx.GetUserId()
                    ?? throw new InvalidOperationException("تعذر تحديد المستخدم الحالي");
                var v = await svc.ApproveAsync(id, userId,
                    req ?? new ApproveVariationRequest(null));
                return Results.Ok(v);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/variations/{id}/reject
        grp.MapPost("/variations/{id:guid}/reject", async (
            Guid id, [FromServices] VariationService svc) =>
        {
            try
            {
                var v = await svc.RejectAsync(id);
                return Results.Ok(v);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // GET /api/contracts/{id}/effective-value
        grp.MapGet("/contracts/{contractId:guid}/effective-value", async (
            Guid contractId, [FromServices] VariationService svc) =>
        {
            var resp = await svc.GetEffectiveValueResponseAsync(contractId);
            return resp is null ? Results.NotFound() : Results.Ok(resp);
        });
    }
}
