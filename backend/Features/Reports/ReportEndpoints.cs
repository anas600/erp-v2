using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Reports;

public static class ReportEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/reports").WithTags("Reports").RequireAuthorization();

        grp.MapGet("/trial-balance", async ([FromQuery] Guid companyId, [FromQuery] DateTime? asOf, [FromQuery] int? level, [FromServices] ReportService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            // Sprint 32 — level 3 (L3 only) is the classic trial balance
            // view. level 4 includes L4 sub-ledgers (expanded).
            var lvl = level ?? 3;
            if (lvl < 3 || lvl > 4) return Results.BadRequest(new { error = "level must be 3 or 4" });
            var report = await svc.GetTrialBalanceAsync(companyId, asOf, lvl);
            return Results.Ok(report);
        });

        grp.MapGet("/income-statement", async (
            [FromQuery] Guid companyId,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromServices] ReportService svc,
            [FromServices] ReportingGate gate) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var toDate = to ?? DateTime.UtcNow;
            // Sprint 32 — gate: TB must balance before IS renders
            var tb = await gate.CheckBalanceAsync(companyId, toDate);
            if (!tb.IsBalanced)
            {
                return Results.Json(new
                {
                    error = "ميزان المراجعة غير متزن — لا يمكن عرض قائمة الدخل",
                    gateFailed = true,
                    totalDebit = tb.TotalDebit,
                    totalCredit = tb.TotalCredit,
                    difference = tb.Difference
                }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            var fromDate = from ?? new DateTime(toDate.Year, 1, 1);
            var report = await svc.GetIncomeStatementAsync(companyId, fromDate, toDate);
            return Results.Ok(report);
        });

        grp.MapGet("/balance-sheet", async ([FromQuery] Guid companyId, [FromQuery] DateTime? asOf, [FromServices] ReportService svc, [FromServices] ReportingGate gate) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var asOfDate = asOf ?? DateTime.UtcNow;
            // Sprint 32 — gate: TB must balance before BS renders
            var tb = await gate.CheckBalanceAsync(companyId, asOfDate);
            if (!tb.IsBalanced)
            {
                return Results.Json(new
                {
                    error = "ميزان المراجعة غير متزن — لا يمكن عرض الميزانية العمومية",
                    gateFailed = true,
                    totalDebit = tb.TotalDebit,
                    totalCredit = tb.TotalCredit,
                    difference = tb.Difference
                }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            var report = await svc.GetBalanceSheetAsync(companyId, asOfDate);
            return Results.Ok(report);
        });

        // General Ledger (دفتر الأستاذ) for a single account.
        // Click an account code in the trial balance to drill into
        // its full transaction history in a date range.
        //
        // Date handling: the frontend sends dates in `YYYY-MM-DD`
        // form (from <input type="date">). ASP.NET binds these as
        // midnight UTC, which causes entries created later in the
        // day to fall OUTSIDE the range. To fix: when the caller
        // sends a date-only value (TimeOfDay == 00:00:00 exactly),
        // we treat `from` as the start of that day and `to` as the
        // end of that day. This is the user's natural intent:
        // "show me everything that happened on 2026-08-05".
        grp.MapGet("/general-ledger", async (
            [FromQuery] Guid companyId,
            [FromQuery] Guid accountId,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromServices] ReportService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            if (accountId == Guid.Empty) return Results.BadRequest(new { error = "accountId required" });

            // Detect date-only values: a date sent as `2026-08-05`
            // arrives as exactly 00:00:00.000 with Kind=Unspecified.
            // If the user picked a day, they want the whole day.
            var now = DateTime.UtcNow;
            var fromDate = from ?? new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var toDate = to ?? now;

            if (fromDate.TimeOfDay == TimeSpan.Zero && fromDate.Kind != DateTimeKind.Utc)
            {
                fromDate = DateTime.SpecifyKind(fromDate.Date, DateTimeKind.Utc);
            }
            if (toDate.TimeOfDay == TimeSpan.Zero && toDate.Kind != DateTimeKind.Utc)
            {
                // End of the picked day, not start. This is the fix.
                toDate = DateTime.SpecifyKind(toDate.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc);
            }

            var report = await svc.GetGeneralLedgerAsync(companyId, accountId, fromDate, toDate);
            return report is null
                ? Results.NotFound(new { error = "Account not found" })
                : Results.Ok(report);
        });

        // Customer Aging (أعمار المدينين)
        grp.MapGet("/customer-aging", async (
            [FromQuery] Guid companyId,
            [FromQuery] DateTime? asOfDate,
            [FromServices] ReportService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var asOf = asOfDate ?? DateTime.UtcNow;
            return Results.Ok(await svc.GetCustomerAgingAsync(companyId, asOf));
        });

        // Supplier Aging (أعمار الدائنين)
        grp.MapGet("/supplier-aging", async (
            [FromQuery] Guid companyId,
            [FromQuery] DateTime? asOfDate,
            [FromServices] ReportService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var asOf = asOfDate ?? DateTime.UtcNow;
            return Results.Ok(await svc.GetSupplierAgingAsync(companyId, asOf));
        });
    }
}
