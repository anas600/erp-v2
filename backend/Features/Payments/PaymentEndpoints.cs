using Microsoft.AspNetCore.Mvc;
using ErpV2.Common;

namespace ErpV2.Features.Payments;

public static class PaymentEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/payments").WithTags("Payments").RequireAuthorization();

        grp.MapGet("/", async ([FromQuery] Guid companyId, [FromQuery] string? status, [FromQuery] Guid? contactId, [FromServices] PaymentService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            return Results.Ok(await svc.GetByCompanyAsync(companyId, status, contactId));
        });

        grp.MapGet("/{id:guid}", async (Guid id, [FromServices] PaymentService svc) =>
        {
            var p = await svc.GetByIdAsync(id);
            return p is null ? Results.NotFound() : Results.Ok(p);
        });

        grp.MapPost("/", async ([FromBody] CreatePaymentVoucherRequest req, [FromServices] PaymentService svc, HttpContext ctx) =>
        {
            try
            {
                var userId = ctx.GetUserId();
                var p = await svc.CreateAsync(req, userId);
                return Results.Created($"/api/payments/{p.Id}", p);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] CreatePaymentVoucherRequest req, [FromServices] PaymentService svc) =>
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

        grp.MapDelete("/{id:guid}", async (Guid id, [FromServices] PaymentService svc) =>
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

        grp.MapPost("/{id:guid}/post", async (Guid id, [FromServices] PaymentService svc, HttpContext ctx) =>
        {
            try
            {
                var userId = ctx.GetUserId();
                var p = await svc.PostAsync(id, userId);
                return p is null ? Results.NotFound() : Results.Ok(p);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
