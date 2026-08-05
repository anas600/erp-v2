using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.FiscalYears;

public static class FiscalYearEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/fiscal-years")
                     .WithTags("Fiscal Years")
                     .RequireAuthorization();

        // GET /api/fiscal-years?companyId=...
        grp.MapGet("/", async (
            [FromQuery] Guid companyId,
            [FromServices] FiscalYearService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            return Results.Ok(await svc.GetYearsAsync(companyId));
        });

        // GET /api/fiscal-years/{id}
        grp.MapGet("/{id:guid}", async (Guid id, [FromServices] FiscalYearService svc) =>
        {
            var year = await svc.GetYearAsync(id);
            return year is null ? Results.NotFound() : Results.Ok(year);
        });

        // POST /api/fiscal-years
        grp.MapPost("/", async (
            [FromBody] CreateFiscalYearRequest req,
            [FromServices] FiscalYearService svc) =>
        {
            try
            {
                var year = await svc.CreateYearAsync(req);
                return Results.Created($"/api/fiscal-years/{year.Id}", year);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/fiscal-years/{id}/close
        grp.MapPost("/{id:guid}/close", async (Guid id, [FromServices] FiscalYearService svc) =>
        {
            try
            {
                var year = await svc.CloseYearAsync(id);
                return year is null ? Results.NotFound() : Results.Ok(year);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // GET /api/fiscal-years/{id}/periods (convenience: periods under a year)
        grp.MapGet("/{id:guid}/periods", async (Guid id, [FromServices] FiscalYearService svc) =>
        {
            var periods = await svc.GetPeriodsAsync(id);
            return Results.Ok(periods);
        });
    }

    /// <summary>
    /// Standalone group for fiscal periods. The period endpoints are
    /// under <c>/api/fiscal-periods</c> so the UI can list all periods
    /// across years without a year filter.
    /// </summary>
    public static void MapPeriods(WebApplication app)
    {
        var grp = app.MapGroup("/api/fiscal-periods")
                     .WithTags("Fiscal Periods")
                     .RequireAuthorization();

        grp.MapGet("/{id:guid}", async (Guid id, [FromServices] FiscalYearService svc) =>
        {
            var p = await svc.GetPeriodAsync(id);
            return p is null ? Results.NotFound() : Results.Ok(p);
        });

        // Lock — any authenticated user can lock (the UI gates this to
        // accountants, but the server doesn't enforce it; if the user
        // has access to the page they can lock).
        grp.MapPost("/{id:guid}/lock", async (Guid id, [FromServices] FiscalYearService svc) =>
        {
            try
            {
                var p = await svc.LockPeriodAsync(id);
                return p is null ? Results.NotFound() : Results.Ok(p);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Unlock — super_admin only. This is the only escape hatch for a
        // locked period, so we restrict it to the highest-privilege role.
        grp.MapPost("/{id:guid}/unlock", async (
            HttpContext ctx,
            Guid id,
            [FromServices] FiscalYearService svc) =>
        {
            if (!ctx.IsSuperAdmin())
            {
                return Results.Json(
                    new { error = "فتح فترة محاسبية مقفلة يتطلب صلاحيات المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var p = await svc.UnlockPeriodAsync(id);
                return p is null ? Results.NotFound() : Results.Ok(p);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
