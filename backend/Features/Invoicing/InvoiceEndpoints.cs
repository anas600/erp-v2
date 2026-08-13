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
            [FromQuery] int? offset,
            [FromQuery] Guid? contactId,
            [FromQuery] string? status,
            [FromQuery] string? invoiceType,
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

            // Sprint 45 — company-wide list with server-side filtering
            // and pagination. Supports ?invoiceType=purchase|sales,
            // ?status=draft|posted|paid|partiallypaid|cancelled, and
            // ?limit=N&offset=M. Returns {items, total} so the
            // frontend can show "page N of M" + total count.
            var effectiveLimit = limit ?? 100;
            var effectiveOffset = offset ?? 0;
            var items = await svc.GetByCompanyAsync(
                companyId,
                effectiveLimit,
                effectiveOffset,
                invoiceType,
                status);
            var total = await svc.CountByCompanyAsync(companyId, invoiceType, status);
            return Results.Ok(new { items, total, limit = effectiveLimit, offset = effectiveOffset });
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

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] CreateInvoiceRequest req, [FromServices] InvoiceService svc) =>
        {
            try
            {
                // Sprint 29 — allow editing draft invoices. The service
                // refuses to touch invoices in any other status, so a
                // posted invoice returns 400 with an Arabic error.
                var inv = await svc.UpdateDraftAsync(id, req);
                return Results.Ok(inv);
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
