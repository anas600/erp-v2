using System.Security.Claims;
using Dapper;
using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Admin;

public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        // Group with RequireAuthorization at the service level,
        // but the cleanup endpoint does an extra check on the
        // super_admin claim. Better than a separate [Authorize]
        // policy because we can return a clearer error message.
        var grp = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization();

        // POST /api/admin/cleanup-transactions
        // Demo-reset endpoint. Requires super_admin.
        // Returns counts of what was deleted.
        grp.MapPost("/cleanup-transactions", async (HttpContext ctx, AdminService svc, ILogger<Program> logger) =>
        {
            // 1) Super-admin gate. The JWT carries `is_super_admin`
            //    as a string claim ("true"/"false"); see
            //    AuthService for the token issuance logic.
            var isSuperAdmin = ctx.User.FindFirst("is_super_admin")?.Value == "true";
            if (!isSuperAdmin)
            {
                return Results.Json(
                    new { error = "هذا الإجراء يتطلب صلاحيات المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // 2) Idempotency: log the request FIRST so even if the
            //    cleanup fails, we have an audit trail of who
            //    tried to wipe the data.
            try
            {
                using var scope = logger.BeginScope(new Dictionary<string, object>
                {
                    ["action"] = "cleanup_transactions",
                    ["user"] = ctx.User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown",
                    ["timestamp"] = DateTime.UtcNow
                });
                logger.LogWarning(
                    "ADMIN CLEANUP: user {Email} is wiping all transactions at {Time}",
                    ctx.User.FindFirst(ClaimTypes.Email)?.Value,
                    DateTime.UtcNow);
            }
            catch { /* logging should never block the request */ }

            // 3) Actually do the cleanup.
            try
            {
                var result = await svc.CleanupAllTransactionsAsync();
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cleanup failed");
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // GET /api/admin/db-stats
        // Read-only: shows row counts for the major tables. Useful
        // before/after cleanup to verify what was deleted. Also
        // requires super_admin.
        grp.MapGet("/db-stats", async (HttpContext ctx, IDbConnectionFactory db) =>
        {
            var isSuperAdmin = ctx.User.FindFirst("is_super_admin")?.Value == "true";
            if (!isSuperAdmin)
            {
                return Results.Json(
                    new { error = "هذا الإجراء يتطلب صلاحيات المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            using var conn = db.CreateConnection();
            var stats = new Dictionary<string, long>
            {
                ["companies"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM companies;"),
                ["users"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM users;"),
                ["accounts"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM accounts;"),
                ["products"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM products;"),
                ["contacts"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM contacts;"),
                ["projects"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM projects;"),
                ["invoices"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM invoices;"),
                ["invoice_lines"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM invoice_lines;"),
                ["journal_entries"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM journal_entries;"),
                ["journal_lines"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM journal_lines;"),
                ["business_rules"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM business_rules;"),
                ["audit_logs"] = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM audit_logs;")
            };
            return Results.Ok(stats);
        });
    }
}
