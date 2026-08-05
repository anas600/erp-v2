using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Invoicing;

public static class InvoiceEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/invoices").WithTags("Invoices").RequireAuthorization();

        grp.MapGet("/", async (
            [FromQuery] Guid companyId,
            [FromQuery] int? limit,
            [FromQuery] Guid? contactId,
            [FromQuery] string? status,
            [FromServices] InvoiceService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });

            // Sprint 25 — contact-scoped view (used by the contact detail
            // page's "Invoices" tab when the frontend doesn't already have
            // the invoice list cached).
            if (contactId.HasValue && contactId.Value != Guid.Empty)
            {
                var asOf = DateTime.UtcNow;
                var filter = string.IsNullOrWhiteSpace(status) ? "all" : status;
                return Results.Ok(await svc.GetByContactAsync(companyId, contactId.Value, filter, asOf));
            }

            // Backwards-compatible: company-wide list. The status filter
            // is ignored for this path (the company-wide view shows
            // everything; the contact-scoped view is the filtered one).
            var data = await svc.GetByCompanyAsync(companyId, limit ?? 100);
            return Results.Ok(data);
        });

        grp.MapGet("/{id:guid}", async (Guid id, [FromServices] InvoiceService svc) =>
        {
            var inv = await svc.GetByIdAsync(id);
            return inv is null ? Results.NotFound() : Results.Ok(inv);
        });

        grp.MapPost("/", async ([FromBody] CreateInvoiceRequest req, [FromServices] InvoiceService svc, HttpContext ctx) =>
        {
            try
            {
                var userId = ctx.GetUserId();
                var inv = await svc.CreateDraftAsync(req, userId);
                return Results.Created($"/api/invoices/{inv.Id}", inv);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPost("/{id:guid}/post", async (Guid id, [FromServices] InvoiceService svc) =>
        {
            try
            {
                var inv = await svc.PostAsync(id);
                return Results.Ok(inv);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPost("/{id:guid}/cancel", async (Guid id, [FromServices] InvoiceService svc) =>
        {
            try
            {
                var ok = await svc.CancelAsync(id);
                return ok ? Results.NoContent() : Results.BadRequest(new { error = "لا يمكن إلغاء فاتورة مرحلة" });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
