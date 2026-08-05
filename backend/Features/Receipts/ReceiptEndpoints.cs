using Microsoft.AspNetCore.Mvc;
using ErpV2.Common;

namespace ErpV2.Features.Receipts;

public static class ReceiptEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/receipts").WithTags("Receipts").RequireAuthorization();

        grp.MapGet("/", async ([FromQuery] Guid companyId, [FromQuery] string? status, ReceiptService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            return Results.Ok(await svc.GetByCompanyAsync(companyId, status));
        });

        grp.MapGet("/{id:guid}", async (Guid id, ReceiptService svc) =>
        {
            var r = await svc.GetByIdAsync(id);
            return r is null ? Results.NotFound() : Results.Ok(r);
        });

        grp.MapPost("/", async ([FromBody] CreateReceiptVoucherRequest req, ReceiptService svc, HttpContext ctx) =>
        {
            try
            {
                var userId = ctx.GetUserId();
                var r = await svc.CreateAsync(req, userId);
                return Results.Created($"/api/receipts/{r.Id}", r);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] CreateReceiptVoucherRequest req, ReceiptService svc) =>
        {
            try
            {
                var r = await svc.UpdateAsync(id, req);
                return r is null ? Results.NotFound() : Results.Ok(r);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapDelete("/{id:guid}", async (Guid id, ReceiptService svc) =>
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

        grp.MapPost("/{id:guid}/post", async (Guid id, ReceiptService svc, HttpContext ctx) =>
        {
            try
            {
                var userId = ctx.GetUserId();
                var r = await svc.PostAsync(id, userId);
                return r is null ? Results.NotFound() : Results.Ok(r);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
