using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Reports;

public static class ReportEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/reports").WithTags("Reports").RequireAuthorization();

        grp.MapGet("/trial-balance", async ([FromQuery] Guid companyId, [FromQuery] DateTime? asOf, ReportService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var report = await svc.GetTrialBalanceAsync(companyId, asOf);
            return Results.Ok(report);
        });

        grp.MapGet("/income-statement", async (
            [FromQuery] Guid companyId,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            ReportService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var fromDate = from ?? new DateTime(DateTime.UtcNow.Year, 1, 1);
            var toDate = to ?? DateTime.UtcNow;
            var report = await svc.GetIncomeStatementAsync(companyId, fromDate, toDate);
            return Results.Ok(report);
        });

        grp.MapGet("/balance-sheet", async ([FromQuery] Guid companyId, [FromQuery] DateTime? asOf, ReportService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var report = await svc.GetBalanceSheetAsync(companyId, asOf);
            return Results.Ok(report);
        });

        // General Ledger (دفتر الأستاذ) for a single account.
        // Click an account code in the trial balance to drill into
        // its full transaction history in a date range.
        grp.MapGet("/general-ledger", async (
            [FromQuery] Guid companyId,
            [FromQuery] Guid accountId,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            ReportService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            if (accountId == Guid.Empty) return Results.BadRequest(new { error = "accountId required" });
            var fromDate = from ?? new DateTime(DateTime.UtcNow.Year, 1, 1);
            var toDate = to ?? DateTime.UtcNow;
            var report = await svc.GetGeneralLedgerAsync(companyId, accountId, fromDate, toDate);
            return report is null
                ? Results.NotFound(new { error = "Account not found" })
                : Results.Ok(report);
        });
    }
}
