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
        grp.MapPost("/cleanup-transactions", async (HttpContext ctx, [FromServices] AdminService svc, ILogger<Program> logger) =>
        {
            // 1) Super-admin gate. The JWT carries `is_super_admin`
            //    as a string claim ("true"/"false"); see
            //    AuthService for the token issuance logic. The
            //    JwtTokenService.IsSuperAdmin() extension method
            //    handles the parse robustly.
            if (!ctx.IsSuperAdmin())
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
                // Render free tier does NOT show stack traces in the
                // response — they get stripped by the runtime. We log
                // a verbose message here so the error is visible in
                // the Render dashboard's "Logs" tab, AND return a
                // rich error object so the user can see what's wrong
                // even without log access.
                logger.LogError(ex, "Cleanup failed: {Type}: {Message}\n{Stack}",
                    ex.GetType().Name, ex.Message, ex.StackTrace);

                var inner = ex.InnerException;
                var depth = 1;
                while (inner != null && depth < 5)
                {
                    logger.LogError("  Inner[{Depth}]: {Type}: {Message}", depth, inner.GetType().Name, inner.Message);
                    inner = inner.InnerException;
                    depth++;
                }

                return Results.BadRequest(new
                {
                    error = ex.Message,
                    type = ex.GetType().Name,
                    inner = ex.InnerException?.Message
                });
            }
        });

        // GET /api/admin/db-stats
        // Read-only: shows row counts for the major tables. Useful
        // before/after cleanup to verify what was deleted. Also
        // requires super_admin.
        grp.MapGet("/db-stats", async (HttpContext ctx, IDbConnectionFactory db) =>
        {
            if (!ctx.IsSuperAdmin())
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

        // GET /api/admin/diagnose
        // Runs each DELETE statement individually and reports which
        // one fails (and with what error). Used to debug the
        // cleanup-transactions endpoint when it 500s.
        // Requires super_admin.
        grp.MapGet("/diagnose", async (HttpContext ctx, IDbConnectionFactory db, ILogger<Program> logger) =>
        {
            if (!ctx.IsSuperAdmin())
            {
                return Results.Json(
                    new { error = "هذا الإجراء يتطلب صلاحيات المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var results = new List<object>();
            string[] statements = {
                "DELETE FROM journal_lines;",
                "DELETE FROM journal_entries;",
                "DELETE FROM invoice_lines;",
                "DELETE FROM invoices;",
                "UPDATE accounts SET balance = 0;"
            };

            using var conn = db.CreateConnection();
            foreach (var stmt in statements)
            {
                try
                {
                    var n = await conn.ExecuteAsync(stmt);
                    results.Add(new { statement = stmt, ok = true, rows = n });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DIAGNOSE: '{Stmt}' failed: {Type}: {Msg}",
                        stmt, ex.GetType().Name, ex.Message);
                    results.Add(new
                    {
                        statement = stmt,
                        ok = false,
                        error = ex.Message,
                        type = ex.GetType().Name,
                        // PG error codes are diagnostic gold:
                        //   23503 = foreign_key_violation
                        //   23505 = unique_violation
                        //   23502 = not_null_violation
                        pgCode = (ex as Npgsql.PostgresException)?.SqlState
                    });
                    // Stop on first failure — we want to fix one
                    // thing at a time, and continuing would
                    // produce cascading failures.
                    break;
                }
            }
            return Results.Ok(new { results });
        });

// ============================================================
        // Sprint 26 — new endpoints
        // ============================================================

        // POST /api/admin/bulk-approve-pending
        // Sprint 30 — full PENDING/DRAFT → POSTED chain for the demo seed.
        // Required because the rule engine (Sprint 15) creates entries
        // as PENDING (accountant review), and the seed wants the trial
        // balance to reflect the postings immediately.
        //
        // Two-step transition per the user's required workflow:
        //   1. PENDING → DRAFT  (approve — accountant sign-off, no balance change)
        //   2. DRAFT   → POSTED (post — hits the General Ledger, updates balances)
        //
        // Uses PostingEngine.PostAsync directly (skips the JournalService
        // wrapper that NRE's in some connection states) and surfaces
        // detailed error info for the seed.
        grp.MapPost("/bulk-approve-pending", async (
            HttpContext ctx,
            [FromQuery] Guid companyId,
            [FromServices] ErpV2.Features.Journal.PostingEngine posting,
            [FromServices] IDbConnectionFactory db) =>
        {
            if (!ctx.IsSuperAdmin()) return Results.Forbid();
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });

            using var conn = db.CreateConnection();
            var pendingIds = (await conn.QueryAsync<Guid>(@"
                SELECT id FROM journal_entries
                WHERE company_id = @companyId AND status IN ('pending', 'draft')
                ORDER BY entry_date, created_at;",
                new { companyId })).ToList();

            var errors = new List<string>();
            int approved = 0;
            foreach (var id in pendingIds)
            {
                try
                {
                    await posting.PostAsync(id);
                    approved++;
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException?.Message ?? "<none>";
                    errors.Add($"{id}: {ex.GetType().Name}: {ex.Message} | inner={inner}");
                }
            }
            return Results.Ok(new
            {
                companyId,
                total = pendingIds.Count,
                approved,
                failed = errors.Count,
                errors
            });
        });

        // POST /api/admin/reseed-coa
        // Sprint 31 — drops ALL accounts (and via CASCADE all journal lines,
        // account_contact_links, business_rules) for the given company, then
        // re-inserts the full 4-level standard COA. Use this to start fresh
        // with the locked L1/L2/L3/L4 architecture. Sub-ledger accounts (L4)
        // and demo contacts/products are NOT re-created here — use seed-demo
        // after for that.
        grp.MapPost("/reseed-coa", async (
            HttpContext ctx,
            [FromQuery] Guid companyId,
            [FromServices] CoaSeeder seeder) =>
        {
            if (!ctx.IsSuperAdmin()) return Results.Forbid();
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            try
            {
                var result = await seeder.ReseedAsync(companyId);
                return Results.Ok(new
                {
                    companyId,
                    l1Count = result.L1Count,
                    l2Count = result.L2Count,
                    l3Count = result.L3Count,
                    message = $"تم إعادة بناء دليل الحسابات: {result.L1Count} L1 + {result.L2Count} L2 + {result.L3Count} L3"
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/admin/cleanup-data
        // Full demo reset. Compared to /cleanup-transactions this also
        // wipes vouchers, intercompany pairs, sub-ledger accounts, and
        // re-opens fiscal periods. Wrapped in a single transaction.
        // Requires super_admin.
        grp.MapPost("/cleanup-data", async (HttpContext ctx, [FromServices] AdminService svc, ILogger<Program> logger) =>
        {
            if (!ctx.IsSuperAdmin())
            {
                return Results.Json(
                    new { error = "هذا الإجراء يتطلب صلاحيات المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                logger.LogWarning(
                    "ADMIN CLEANUP-DATA: user {Email} is wiping all data at {Time}",
                    ctx.User.FindFirst(ClaimTypes.Email)?.Value,
                    DateTime.UtcNow);
            }
            catch { /* logging should never block the request */ }

            try
            {
                var result = await svc.CleanupDataAsync();
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CleanupData failed: {Type}: {Message}\n{Stack}",
                    ex.GetType().Name, ex.Message, ex.StackTrace);
                return Results.BadRequest(new
                {
                    error = ex.Message,
                    type = ex.GetType().Name,
                    inner = ex.InnerException?.Message
                });
            }
        });

        // POST /api/admin/seed-demo-data?companyId=...
        // Creates 5 customers + 3 suppliers + 10 invoices + 5 receipts +
        // 2 payments with realistic Libyan data. Requires super_admin.
        // Idempotent at the contact level (re-running won't duplicate
        // customers/suppliers), but invoices/vouchers will accumulate
        // (use /cleanup-data first if you want a clean slate).
        grp.MapPost("/seed-demo-data", async (
            HttpContext ctx,
            [FromQuery] Guid companyId,
            [FromServices] DemoDataSeeder seeder,
            [FromServices] IDbConnectionFactory db,
            [FromServices] ErpV2.Features.Journal.PostingEngine posting) =>
        {
            if (!ctx.IsSuperAdmin())
            {
                return Results.Json(
                    new { error = "هذا الإجراء يتطلب صلاحيات المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (companyId == Guid.Empty)
                return Results.BadRequest(new { error = "companyId required" });

            // Guard: company must exist.
            using (var conn = db.CreateConnection())
            {
                var exists = await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM companies WHERE id = @id);",
                    new { id = companyId });
                if (!exists)
                    return Results.BadRequest(new { error = "الشركة غير موجودة" });
            }

            var userId = ctx.GetUserId();

            try
            {
                var result = await seeder.SeedAsync(companyId, userId);
                // Bulk-approve pending entries via the dedicated
                // endpoint (uses PostingEngine directly, bypasses
                // the NRE that hits the in-process ApproveAsync loop).
                // This way the client gets a fully-posted dataset
                // in a single seed call.
                try
                {
                    using var conn2 = db.CreateConnection();
                    var pendingIds = (await conn2.QueryAsync<Guid>(@"
                        SELECT id FROM journal_entries
                        WHERE company_id = @companyId AND status IN ('pending', 'draft');",
                        new { companyId })).ToList();
                    int approved = 0;
                    var approveErrors = new List<string>();
                    foreach (var id in pendingIds)
                    {
                        try
                        {
                            await posting.PostAsync(id);
                            approved++;
                        }
                        catch (Exception aex)
                        {
                            approveErrors.Add($"{id}: {aex.GetType().Name}: {aex.Message}");
                        }
                    }
                    // Augment the result with the approval outcome.
                    // We use a small wrapper record (anonymous) and
                    // project to a new shape so the client sees both
                    // seed stats and approve stats.
                    return Results.Ok(new
                    {
                        seed = result,
                        pendingApproved = new
                        {
                            total = pendingIds.Count,
                            approved,
                            failed = approveErrors.Count,
                            errors = approveErrors
                        }
                    });
                }
                catch (Exception bex)
                {
                    return Results.Ok(new
                    {
                        seed = result,
                        pendingApproveError = bex.Message
                    });
                }
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    error = ex.Message,
                    type = ex.GetType().Name,
                    inner = ex.InnerException?.Message
                });
            }
        });
    }
}
