using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 57 — Print view endpoints.
/// Two endpoints:
///   - GET /api/print/billings/{id}          → JSON data (for the SPA print page)
///   - GET /api/print/billings/{id}/html     → server-rendered HTML (for direct print)
/// The JSON is what the SPA uses. The HTML is a fallback for
/// "open in new tab" / "save as PDF" without SPA auth.
/// </summary>
public static class PrintViewEndpoints
{
    public static void MapPrintViewEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/print").WithTags("PrintView");

        grp.MapGet("/billings/{id:guid}", async (
            Guid id,
            [FromServices] PrintViewService svc,
            HttpContext ctx) =>
        {
            if (!ctx.IsSuperAdmin())
                return Results.Json(
                    new { error = "يتطلب صلاحية المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            try
            {
                var data = await svc.GetPrintViewAsync(id);
                return Results.Ok(data);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
