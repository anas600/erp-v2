using ErpV2.Features.Invoicing;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Intercompany;

/// <summary>
/// Intercompany endpoints — Sprint 24.
///
/// Sister-company (الشركات الشقيقة) transaction support. The actual
/// mirror-invoice creation lives in InvoiceService.PostAsync (it has
/// to — the mirror is a side effect of posting the primary). This
/// endpoint file exposes the read-side of the feature:
///   - List pairs for a company
///   - Get one pair (with both invoice details)
///   - Reverse a pair (creates reversing journal entries in both
///     companies and marks the pair as 'reversed')
/// </summary>
public static class IntercompanyEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/intercompany").WithTags("Intercompany").RequireAuthorization();

        // GET /api/intercompany/pairs?companyId=...&fromDate=...&toDate=...
        // Lists every pair where either side belongs to the given
        // company. Date range is optional; defaults to "everything".
        grp.MapGet("/pairs", async (
            [FromQuery] Guid companyId,
            [FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate,
            [FromServices] InvoiceService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var data = await svc.GetIntercompanyPairsAsync(companyId, fromDate, toDate);
            return Results.Ok(data);
        });

        // GET /api/intercompany/pairs/{id}
        // Returns the pair + both invoice DTOs (primary + mirror).
        grp.MapGet("/pairs/{id:guid}", async (Guid id, [FromServices] InvoiceService svc) =>
        {
            var data = await svc.GetIntercompanyPairAsync(id);
            return data is null ? Results.NotFound() : Results.Ok(data);
        });

        // POST /api/intercompany/pairs/{id}/reverse
        // Creates reversing journal entries in BOTH companies and
        // marks the pair (and both invoices) as 'reversed'. This
        // is the cancellation flow for an intercompany transaction.
        grp.MapPost("/pairs/{id:guid}/reverse", async (Guid id, [FromServices] InvoiceService svc) =>
        {
            try
            {
                var result = await svc.ReverseIntercompanyPairAsync(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
