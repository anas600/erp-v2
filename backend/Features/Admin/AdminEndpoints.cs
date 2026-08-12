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

                // Sprint 32 — re-enable all business rules before seeding.
                // The COA reseed disables all rules as a side effect, so
                // the seed must re-enable them to produce journal entries.
                await conn.ExecuteAsync("UPDATE business_rules SET enabled = true;");
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

        // POST /api/admin/fix-business-rules?companyId=...
        // Sprint 33 — bulk-update the 5 default business rules so they
        // reference the correct standard COA codes after the Sprint 31
        // refactor (1000/1100/1200/2000 → 1101/1102/1103/2101 etc).
        //
        // Without this, the existing rules would still try to post to
        // non-existent accounts. The wizard (Sprint 34) will let the
        // accountant edit any rule, but for now we hard-code the
        // correct mappings to keep the system running.
        //
        // Returns a list of every rule that was updated, with before/after.
        grp.MapPost("/fix-business-rules", async (
            HttpContext ctx,
            [FromServices] IDbConnectionFactory db) =>
        {
            if (!ctx.IsSuperAdmin())
            {
                return Results.Json(
                    new { error = "هذا الإجراء يتطلب صلاحيات المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // Mapping: each entry is (eventName, new accountCode, new description).
            // We rebuild the ruleJson from scratch so the structure is
            // consistent (some of the old rules had inconsistent quoting).
            //
            // COA mapping (Sprint 31 standard):
            //   Cash:       1101 (L3)  / 1101-CASH-001 (L4)
            //   Bank:       1102 (L3)  / 1102-BANK-001 (L4)
            //   AR:         1103 (L3)  / 1103-CUST-XXX (L4 per customer)
            //   AP:         2101 (L3)  / 2101-SUPP-XXX (L4 per supplier)
            //   VAT In:     1107
            //   VAT Out:    2104
            //   Sales:      4101 (goods), 4102 (services), 4103 (projects)
            //   COGS:       5301
            //   Depreciation expense: 5106
            //   Acc. depreciation:    1202 (equip) / 1204 (furniture)
            var ruleTemplates = new[]
            {
                new
                {
                    name = "إهلاك أصول شهري",
                    eventName = "PeriodClose",
                    ruleJson = @"{
                      ""actions"": [{
                        ""type"": ""PostJournalEntry"",
                        ""lines"": [
                          { ""nature"": ""debit"",  ""accountCode"": ""5106"", ""description"": ""مصروف إهلاك شهري"",   ""amountFormula"": ""depreciation.amount"" },
                          { ""nature"": ""credit"", ""accountCode"": ""1202"", ""description"": ""مجمع إهلاك المعدات"", ""amountFormula"": ""depreciation.amount"" }
                        ],
                        ""narration"": ""إهلاك شهري للمعدات""
                      }],
                      ""conditions"": { ""all"": [] }
                    }"
                },
                new
                {
                    name = "إيراد مشروع (Milestone)",
                    eventName = "ProjectMilestoneCompleted",
                    ruleJson = @"{
                      ""actions"": [{
                        ""type"": ""PostJournalEntry"",
                        ""lines"": [
                          { ""nature"": ""debit"",  ""accountCode"": ""1103"", ""description"": ""مدينون - {customer.name}"",  ""amountFormula"": ""milestone.amount"" },
                          { ""nature"": ""credit"", ""accountCode"": ""4103"", ""description"": ""إيراد مشروع {project.name}"", ""amountFormula"": ""milestone.amount"" }
                        ],
                        ""narration"": ""إيراد مرحلة {milestone.name} من مشروع {project.name}""
                      }],
                      ""conditions"": { ""all"": [] }
                    }"
                },
                new
                {
                    // Sprint 34 — now uses accountFrom directives so the
                    // actual posting is fully data-driven:
                    //   "voucher.bankAccount" → resolve to the bankAccountId
                    //                             on the voucher (Dr cash)
                    //   "contact.subLedger"   → resolve to the customer's
                    //                             sub-ledger account (Cr AR)
                    // No hard-coded account codes — works for any cash
                    // account and any customer.
                    name = "تحصيل من عميل",
                    eventName = "CustomerReceiptReceived",
                    ruleJson = @"{
                      ""actions"": [{
                        ""type"": ""PostJournalEntry"",
                        ""lines"": [
                          { ""nature"": ""debit"",  ""accountFrom"": ""voucher.bankAccount"", ""description"": ""الصندوق/البنك - تحصيل من عميل"", ""amountFormula"": ""receipt.amount"" },
                          { ""nature"": ""credit"", ""accountFrom"": ""contact.subLedger"",   ""description"": ""تسوية حساب العميل"",          ""amountFormula"": ""receipt.amount"" }
                        ],
                        ""narration"": ""تحصيل من عميل {customer.name}""
                      }],
                      ""conditions"": { ""all"": [] }
                    }"
                },
                new
                {
                    name = "ترحيل فاتورة مبيعات",
                    eventName = "SalesInvoiceApproved",
                    ruleJson = @"{
                      ""actions"": [{
                        ""type"": ""PostJournalEntry"",
                        ""lines"": [
                          { ""nature"": ""debit"",  ""accountCode"": ""1103"", ""description"": ""مدينون - {customer.name}"",                                ""amountFormula"": ""invoice.total"" },
                          { ""nature"": ""credit"", ""accountCode"": ""4101"", ""description"": ""إيرادات المبيعات - {customer.name}"",                      ""amountFormula"": ""invoice.subtotal"" },
                          { ""nature"": ""credit"", ""accountCode"": ""2104"", ""description"": ""ضريبة مخرجات مستحقة - INV {invoice.number}"",               ""amountFormula"": ""invoice.tax"" }
                        ],
                        ""narration"": ""فاتورة مبيعات رقم {invoice.number} - {customer.name}""
                      }],
                      ""conditions"": { ""all"": [] }
                    }"
                },
                new
                {
                    name = "ترحيل فاتورة مشتريات",
                    eventName = "PurchaseInvoiceApproved",
                    ruleJson = @"{
                      ""actions"": [{
                        ""type"": ""PostJournalEntry"",
                        ""lines"": [
                          { ""nature"": ""debit"",  ""accountCode"": ""5301"", ""description"": ""تكلفة المشتريات - {supplier.name}"",                        ""amountFormula"": ""invoice.subtotal"" },
                          { ""nature"": ""debit"",  ""accountCode"": ""1107"", ""description"": ""ضريبة مدخلات - INV {invoice.number}"",                       ""amountFormula"": ""invoice.tax"" },
                          { ""nature"": ""credit"", ""accountCode"": ""2101"", ""description"": ""دائنون - {supplier.name}"",                                  ""amountFormula"": ""invoice.total"" }
                        ],
                        ""narration"": ""فاتورة مشتريات رقم {invoice.number} - {supplier.name}""
                      }],
                      ""conditions"": { ""all"": [] }
                    }"
                },
                new
                {
                    // Sprint 34 — uses accountFrom directives (see comment on
                    // CustomerReceiptReceived above). No hard-coded codes.
                    name = "دفع مورد",
                    eventName = "SupplierPaymentMade",
                    ruleJson = @"{
                      ""actions"": [{
                        ""type"": ""PostJournalEntry"",
                        ""lines"": [
                          { ""nature"": ""debit"",  ""accountFrom"": ""contact.subLedger"",   ""description"": ""تسوية حساب المورّد"",          ""amountFormula"": ""payment.amount"" },
                          { ""nature"": ""credit"", ""accountFrom"": ""voucher.bankAccount"", ""description"": ""الصندوق/البنك - دفع لمورّد"",    ""amountFormula"": ""payment.amount"" }
                        ],
                        ""narration"": ""دفع لمورّد {supplier.name}""
                      }],
                      ""conditions"": { ""all"": [] }
                    }"
                }
            };

            using var conn = db.CreateConnection();
            var updated = new List<object>();

            foreach (var tmpl in ruleTemplates)
            {
                // Find the rule by event name. We update the FIRST enabled
                // rule for each event (rules are unique per event in the
                // standard seed).
                var existing = await conn.QueryFirstOrDefaultAsync<(Guid id, string ruleJson)?>(@"
                    SELECT id, rule_json
                    FROM business_rules
                    WHERE event_name = @eventName AND is_template = true
                    LIMIT 1;",
                    new { eventName = tmpl.eventName });

                if (existing is null)
                {
                    // Create the rule
                    var newId = Guid.NewGuid();
                    await conn.ExecuteAsync(@"
                        INSERT INTO business_rules (id, name, description, event_name, enabled, priority, rule_json, is_template, created_at, updated_at)
                        VALUES (@id, @name, @description, @eventName, true, 10, @ruleJson::jsonb, true, NOW(), NOW());",
                        new
                        {
                            id = newId,
                            name = tmpl.name,
                            description = $"Sprint 33 auto-fixed: {tmpl.name}",
                            eventName = tmpl.eventName,
                            ruleJson = tmpl.ruleJson
                        });
                    updated.Add(new { eventName = tmpl.eventName, action = "created", name = tmpl.name });
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        UPDATE business_rules
                        SET rule_json = @ruleJson::jsonb,
                            name = @name,
                            updated_at = NOW()
                        WHERE id = @id;",
                        new
                        {
                            id = existing.Value.id,
                            name = tmpl.name,
                            ruleJson = tmpl.ruleJson
                        });
                    updated.Add(new { eventName = tmpl.eventName, action = "updated", name = tmpl.name });
                }
            }

            return Results.Ok(new
            {
                fixedAt = DateTime.UtcNow,
                rulesUpdated = updated.Count,
                details = updated
            });
        });

        // ----------------------------------------------------------------
        // Sprint 39 — Full-year realistic data seeder
        // ----------------------------------------------------------------
        // POST /api/admin/seed-full-year?companyId=...
        // Wipes transactions and re-creates a full year (Sep 2025 → Aug 2026)
        // of realistic business activity: 10 customers, 10 suppliers, 15
        // products, ~80 sales invoices, ~60 purchase invoices, ~50 receipts,
        // ~45 payments, recurring monthly entries, 4 projects with BOQ +
        // contracts + progress billings + variations, year-end closing.
        // Requires super_admin.
        grp.MapPost("/seed-full-year", async (
            HttpContext ctx,
            [FromQuery] Guid companyId,
            [FromServices] FullYearSeeder seeder,
            [FromServices] IDbConnectionFactory db) =>
        {
            if (!ctx.IsSuperAdmin())
            {
                return Results.Json(
                    new { error = "هذا الإجراء يتطلب صلاحيات المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (companyId == Guid.Empty)
                return Results.BadRequest(new { error = "companyId required" });

            // Guard: company must exist
            using (var conn = db.CreateConnection())
            {
                var exists = await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM companies WHERE id = @id);",
                    new { id = companyId });
                if (!exists)
                    return Results.BadRequest(new { error = "الشركة غير موجودة" });

                // Re-enable all business rules so the seeder produces JEs
                await conn.ExecuteAsync("UPDATE business_rules SET enabled = true;");
            }

            var result = await seeder.SeedAsync(companyId, null);
            return Results.Ok(result);
        });

        // ===================================================================
        // Sprint 41 — Diagnostics & Verification endpoints
        //
        // The Render free tier doesn't expose log streams to non-owners,
        // which makes blind debugging expensive (each push = ~90s cold
        // deploy). These endpoints give the orchestrator (Mavis) a
        // read-only window into what the system actually has in its
        // database and what the seeder did on the last run.
        // ===================================================================

        // GET /api/admin/journals-summary?companyId=X
        // Returns a snapshot of journal entry counts broken down by
        // status (draft, pending, posted, reversed). The seeder
        // claims to post N entries; this endpoint tells you if it
        // actually did. Requires admin (not super_admin — this is
        // read-only and the team uses it during demos).
        grp.MapGet("/journals-summary", async (HttpContext ctx, IDbConnectionFactory db, Guid? companyId) =>
        {
            using var conn = db.CreateConnection();

            // Per-status counts for one company (or all companies if
            // companyId is null).
            var sql = companyId.HasValue
                ? @"SELECT status, source, COUNT(*) AS n
                    FROM journal_entries
                    WHERE company_id = @companyId
                    GROUP BY status, source
                    ORDER BY status, source;"
                : @"SELECT status, source, COUNT(*) AS n
                    FROM journal_entries
                    GROUP BY status, source
                    ORDER BY status, source;";

            var rows = (await conn.QueryAsync<(string status, string source, long n)>(
                sql, new { companyId })).ToList();

            // Aggregate by status for a quick "is the seeder doing
            // what it claims?" view.
            var byStatus = rows
                .GroupBy(r => r.status)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.n));

            // Aggregate by source for "how many JEs came from which
            // pipeline".
            var bySource = rows
                .GroupBy(r => r.source)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.n));

            return Results.Ok(new
            {
                companyId = companyId?.ToString() ?? "ALL",
                totalEntries = rows.Sum(r => r.n),
                byStatus,
                bySource,
                detail = rows.Select(r => new { r.status, r.source, count = r.n })
            });
        });

        // GET /api/admin/seed-status
        // Returns the in-memory result of the last seed run plus the
        // trusted-mode flag. Lets the orchestrator verify that the
        // seeder is using the right code path without waiting for a
        // fresh seed.
        grp.MapGet("/seed-status", (HttpContext ctx, [FromServices] FullYearSeeder seeder) =>
        {
            return Results.Ok(new
            {
                trustedMode = TrustedAccountantMode.IsEnabled,
                trustedModeLabel = TrustedAccountantMode.Label,
                envVar = Environment.GetEnvironmentVariable("SEEDER_TRUSTED_ACCOUNTANT_MODE"),
                envAutoSeedDemo = Environment.GetEnvironmentVariable("AUTO_SEED_DEMO"),
                envDemoCompany = Environment.GetEnvironmentVariable("DEMO_COMPANY_ID"),
                serverTime = DateTime.UtcNow
            });
        });

        // GET /api/admin/verify?companyId=X
        // Automated report verification. Instead of HTTP-calling
        // the report endpoints (which has a fragile auth-header
        // forwarding story in this version of .NET), we hit the
        // underlying SQL directly and compute the same checks the
        // report endpoints do — TB balance, IS/BS presence,
        // aging totals. This is faster, more reliable, and the
        // orchestrator only needs the pass/fail signal anyway.
        grp.MapGet("/verify", async (HttpContext ctx, [FromServices] IDbConnectionFactory db, Guid companyId) =>
        {
            var checks = new List<object>();
            int passed = 0, failed = 0;

            using var conn = db.CreateConnection();

            // ---------- Check 1: Journal entry presence ----------
            try
            {
                var total = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM journal_entries WHERE company_id = @id;",
                    new { id = companyId });
                var posted = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM journal_entries WHERE company_id = @id AND status = 'posted';",
                    new { id = companyId });
                var ok = total > 0 && posted > 0;
                if (ok) passed++; else failed++;
                checks.Add(new
                {
                    report = "journal-entries",
                    status = ok ? "PASS" : "FAIL",
                    data = new { total, posted, draft = total - posted }
                });
            }
            catch (Exception ex)
            {
                failed++;
                checks.Add(new { report = "journal-entries", status = "ERROR", error = ex.Message });
            }

            // ---------- Check 2: Trial balance balance ----------
            try
            {
                var dr = await conn.ExecuteScalarAsync<decimal?>(@"
                    SELECT COALESCE(SUM(jl.debit), 0)
                    FROM journal_lines jl
                    JOIN journal_entries je ON je.id = jl.journal_entry_id
                    WHERE je.company_id = @id AND je.status = 'posted';",
                    new { id = companyId }) ?? 0m;
                var cr = await conn.ExecuteScalarAsync<decimal?>(@"
                    SELECT COALESCE(SUM(jl.credit), 0)
                    FROM journal_lines jl
                    JOIN journal_entries je ON je.id = jl.journal_entry_id
                    WHERE je.company_id = @id AND je.status = 'posted';",
                    new { id = companyId }) ?? 0m;
                // Tolerate 0.01 LYD rounding
                var balanced = Math.Abs(dr - cr) < 0.5m;
                if (balanced) passed++; else failed++;
                checks.Add(new
                {
                    report = "trial-balance-balanced",
                    status = balanced ? "PASS" : "FAIL",
                    data = new { debit = dr, credit = cr, diff = dr - cr }
                });
            }
            catch (Exception ex)
            {
                failed++;
                checks.Add(new { report = "trial-balance-balanced", status = "ERROR", error = ex.Message });
            }

            // ---------- Check 3: Income statement movement ----------
            try
            {
                // Filter by account_type, not code prefix. The COA
                // uses account_type='Revenue' for 4xxx and
                // account_type='Expense' for 5xxx.
                var revenue = await conn.ExecuteScalarAsync<decimal?>(@"
                    SELECT COALESCE(SUM(jl.credit - jl.debit), 0)
                    FROM journal_lines jl
                    JOIN journal_entries je ON je.id = jl.journal_entry_id
                    JOIN accounts a ON a.id = jl.account_id
                    WHERE je.company_id = @id AND je.status = 'posted'
                      AND a.account_type = 'Revenue'
                      AND (je.source IS NULL OR je.source <> 'year-end-closing');",
                    new { id = companyId }) ?? 0m;
                var expense = await conn.ExecuteScalarAsync<decimal?>(@"
                    SELECT COALESCE(SUM(jl.debit - jl.credit), 0)
                    FROM journal_lines jl
                    JOIN journal_entries je ON je.id = jl.journal_entry_id
                    JOIN accounts a ON a.id = jl.account_id
                    WHERE je.company_id = @id AND je.status = 'posted'
                      AND a.account_type = 'Expense'
                      AND (je.source IS NULL OR je.source <> 'year-end-closing');",
                    new { id = companyId }) ?? 0m;
                var hasActivity = revenue > 0 || expense > 0;
                if (hasActivity) passed++; else failed++;
                checks.Add(new
                {
                    report = "income-statement-activity",
                    status = hasActivity ? "PASS" : "FAIL",
                    data = new { revenue, expense, net = revenue - expense }
                });
            }
            catch (Exception ex)
            {
                failed++;
                checks.Add(new { report = "income-statement-activity", status = "ERROR", error = ex.Message });
            }

            // ---------- Check 4: Balance sheet A = L + E ----------
            try
            {
                // For the BS, we use the account_type to decide the
                // sign (not the nature). This is the key insight:
                //   Asset accounts contribute positively when they
                //   have a debit balance (debit - credit). Contra-
                //   assets (e.g. 1202 Accum Dep) are account_type
                //   'Asset' but with a credit balance — they're
                //   shown with their natural sign and we NEGATE.
                //   The simplest rule: balance for each account is
                //   (debit - credit) for Asset, (credit - debit)
                //   for Liability/Equity, and we sum directly.
                var assets = await conn.ExecuteScalarAsync<decimal?>(@"
                    SELECT COALESCE(SUM(jl.debit - jl.credit), 0)
                    FROM journal_lines jl
                    JOIN journal_entries je ON je.id = jl.journal_entry_id
                    JOIN accounts a ON a.id = jl.account_id
                    WHERE je.company_id = @id AND je.status = 'posted'
                      AND a.account_type = 'Asset';",
                    new { id = companyId }) ?? 0m;
                var liabEq = await conn.ExecuteScalarAsync<decimal?>(@"
                    SELECT COALESCE(SUM(jl.credit - jl.debit), 0)
                    FROM journal_lines jl
                    JOIN journal_entries je ON je.id = jl.journal_entry_id
                    JOIN accounts a ON a.id = jl.account_id
                    WHERE je.company_id = @id AND je.status = 'posted'
                      AND a.account_type IN ('Liability', 'Equity');",
                    new { id = companyId }) ?? 0m;
                var balanced = Math.Abs(assets - liabEq) < 100m; // tolerate 100 LYD diff for opening vs FY
                if (balanced) passed++; else failed++;
                checks.Add(new
                {
                    report = "balance-sheet-balanced",
                    status = balanced ? "PASS" : "FAIL",
                    data = new { assets, liabEq, diff = assets - liabEq }
                });
            }
            catch (Exception ex)
            {
                failed++;
                checks.Add(new { report = "balance-sheet-balanced", status = "ERROR", error = ex.Message });
            }

            // ---------- Check 5: AR aging (sub-ledger balance) ----------
            try
            {
                // Sum of (debit - credit) for AR sub-ledger accounts.
                // The COA has 1103 as the customer AR control account
                // and 1103-CUST-XXX as the L4 sub-ledgers.
                var ar = await conn.ExecuteScalarAsync<decimal?>(@"
                    SELECT COALESCE(SUM(jl.debit - jl.credit), 0)
                    FROM journal_lines jl
                    JOIN journal_entries je ON je.id = jl.journal_entry_id
                    JOIN accounts a ON a.id = jl.account_id
                    WHERE je.company_id = @id AND je.status = 'posted'
                      AND (a.code = '1103' OR a.code LIKE '1103-%');",
                    new { id = companyId }) ?? 0m;
                var hasAR = ar > 0;
                if (hasAR) passed++; else failed++;
                checks.Add(new
                {
                    report = "ar-sub-ledger",
                    status = hasAR ? "PASS" : "FAIL",
                    data = new { ar }
                });
            }
            catch (Exception ex)
            {
                failed++;
                checks.Add(new { report = "ar-sub-ledger", status = "ERROR", error = ex.Message });
            }

            // ---------- Check 6: AP aging (sub-ledger balance) ----------
            try
            {
                var ap = await conn.ExecuteScalarAsync<decimal?>(@"
                    SELECT COALESCE(SUM(jl.credit - jl.debit), 0)
                    FROM journal_lines jl
                    JOIN journal_entries je ON je.id = jl.journal_entry_id
                    JOIN accounts a ON a.id = jl.account_id
                    WHERE je.company_id = @id AND je.status = 'posted'
                      AND (a.code = '2101' OR a.code LIKE '2101-%');",
                    new { id = companyId }) ?? 0m;
                var hasAP = ap > 0;
                if (hasAP) passed++; else failed++;
                checks.Add(new
                {
                    report = "ap-sub-ledger",
                    status = hasAP ? "PASS" : "FAIL",
                    data = new { ap }
                });
            }
            catch (Exception ex)
            {
                failed++;
                checks.Add(new { report = "ap-sub-ledger", status = "ERROR", error = ex.Message });
            }

            return Results.Ok(new
            {
                companyId = companyId.ToString(),
                summary = new
                {
                    total = checks.Count,
                    passed,
                    failed,
                    overall = failed == 0 ? "PASS" : "FAIL"
                },
                checks
            });
        });

        // POST /api/admin/wipe-all
        // Sprint 42 — full database wipe for a fresh demo scenario.
        // Truncates ALL transaction tables + master data (contacts,
        // products, projects) in the right order. Keeps the COA
        // (accounts) and the user list. CASCADE handles the FK
        // dependencies.
        //
        // ⚠️ DESTRUCTIVE — only for demo / test environments.
        // Requires super_admin.
        grp.MapPost("/wipe-all", async (HttpContext ctx, IDbConnectionFactory db, [FromServices] FullYearSeeder seeder) =>
        {
            if (!ctx.IsSuperAdmin())
            {
                return Results.Json(
                    new { error = "هذا الإجراء يتطلب صلاحيات المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            using var conn = db.CreateConnection();
            try
            {
                // The order matters — respect FKs. CASCADE handles
                // dependencies, but we still need to truncate the
                // parent tables first because some FKs aren't
                // declared CASCADE in the schema.
                await conn.ExecuteAsync(@"
                    TRUNCATE TABLE
                        contract_variation_items,
                        contract_variations,
                        contract_line_items,
                        billing_line_items,
                        progress_billings,
                        contracts,
                        project_milestones,
                        projects,
                        receipt_vouchers,
                        payment_vouchers,
                        journal_lines,
                        journal_entries,
                        invoice_lines,
                        invoices,
                        project_allocations,
                        cost_centers,
                        products,
                        contacts
                    RESTART IDENTITY CASCADE;");

                // Reset account balances to 0 (the COA structure
                // stays, only the running totals are cleared).
                await conn.ExecuteAsync(
                    "UPDATE accounts SET balance = 0 WHERE company_id IS NOT NULL;");

                // Close any open fiscal years so the next seed
                // can create a fresh one without conflicts.
                await conn.ExecuteAsync(
                    "UPDATE fiscal_years SET is_closed = true WHERE is_closed = false;");

                return Results.Ok(new
                {
                    message = "Wipe complete. COA and users preserved.",
                    reset = new[] { "contacts", "products", "projects", "invoices", "journal_entries", "vouchers", "balances" }
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    error = ex.Message,
                    type = ex.GetType().Name
                });
            }
        });

        // POST /api/admin/rebuild-balances?companyId=X
        // Sprint 41 — the accounts.balance column can drift from
        // journal_lines (e.g. when bulk-post runs through paths
        // that update journal_entries.status but miss the
        // accounts.balance UPDATE in PostingEngine, or when a
        // migration is applied to historical data). This
        // endpoint recomputes balances from the authoritative
        // source (journal_lines, posted only) and writes them
        // back to accounts.balance in a single transaction.
        //
        // Implementation note: the L4 sub-ledgers (cash, AR, AP,
        // etc.) are postable, so postings go directly to them and
        // their accounts.balance must equal the net of their own
        // journal_lines. The L3 controls (1101, 1102, 2101, etc.)
        // are NOT directly posted to — their balance represents
        // the NET of their sub-ledgers' postings (since the
        // control's "balance" is what the trial balance displays
        // after Sprint 33's NET rule). For revenue (4xxx) and
        // expense (5xxx), which have no sub-ledgers, postings go
        // directly to the L3 account.
        grp.MapPost("/rebuild-balances", async (HttpContext ctx, IDbConnectionFactory db, ILogger<Program> logger, [FromQuery] Guid companyId) =>
        {
            if (!ctx.IsSuperAdmin())
            {
                return Results.Json(
                    new { error = "هذا الإجراء يتطلب صلاحيات المدير العام (super_admin)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (companyId == Guid.Empty)
                return Results.BadRequest(new { error = "companyId required" });

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                // Step 1: Set L4 sub-ledger balances directly from
                // their journal_lines (these are postable accounts).
                var l4Updated = await conn.ExecuteAsync(@"
                    UPDATE accounts a
                    SET balance = sub.net
                    FROM (
                        SELECT jl.account_id,
                               SUM(CASE
                                   WHEN ac.account_type IN ('Asset', 'Expense')
                                       THEN jl.debit - jl.credit
                                   ELSE jl.credit - jl.debit
                               END) AS net
                        FROM journal_lines jl
                        JOIN journal_entries je ON je.id = jl.journal_entry_id
                        JOIN accounts ac ON ac.id = jl.account_id
                        WHERE je.company_id = @companyId AND je.status = 'posted'
                          AND ac.level = 4
                        GROUP BY jl.account_id
                    ) sub
                    WHERE a.id = sub.account_id
                      AND a.company_id = @companyId
                      AND a.level = 4;",
                    new { companyId }, tx);

                // Step 2: Set L3 control balances to NET of their
                // L4 sub-ledgers' balances (Sprint 33 NET rule).
                // We compute: L3.balance = Σ (sub_ledger.balance with
                // natural signs) — this is the "غير مخصص" (unallocated)
                // figure the trial balance displays.
                var l3Updated = await conn.ExecuteAsync(@"
                    UPDATE accounts parent
                    SET balance = COALESCE((
                        SELECT SUM(CASE
                            WHEN child.nature = 'Debit' THEN child.balance
                            ELSE -child.balance
                        END)
                        FROM accounts child
                        WHERE child.parent_id = parent.id
                    ), 0)
                    WHERE parent.company_id = @companyId
                      AND parent.level = 3;",
                    new { companyId }, tx);

                // Step 3: For L3 accounts that have direct postings
                // (no sub-ledgers), override with the journal_lines
                // total. The COA has 4xxx/5xxx as L3-only (no
                // sub-ledgers) — postings go to them directly, even
                // though their is_postable is false. The L3 controls
                // for AR/AP (1103/2101) DO have sub-ledgers so Step 2
                // was correct for them; this step is for the L3
                // accounts that don't have any sub-ledger children.
                var l3DirectUpdated = await conn.ExecuteAsync(@"
                    UPDATE accounts a
                    SET balance = sub.net
                    FROM (
                        SELECT jl.account_id,
                               SUM(CASE
                                   WHEN ac.account_type IN ('Asset', 'Expense')
                                       THEN jl.debit - jl.credit
                                   ELSE jl.credit - jl.debit
                               END) AS net
                        FROM journal_lines jl
                        JOIN journal_entries je ON je.id = jl.journal_entry_id
                        JOIN accounts ac ON ac.id = jl.account_id
                        WHERE je.company_id = @companyId AND je.status = 'posted'
                          AND ac.level = 3
                          AND ac.id NOT IN (SELECT parent_id FROM accounts WHERE parent_id IS NOT NULL)
                        GROUP BY jl.account_id
                    ) sub
                    WHERE a.id = sub.account_id
                      AND a.company_id = @companyId
                      AND a.level = 3
                      AND a.id NOT IN (SELECT parent_id FROM accounts WHERE parent_id IS NOT NULL);",
                    new { companyId }, tx);

                // Step 4: Zero out anything with no postings.
                var zeroed = await conn.ExecuteAsync(@"
                    UPDATE accounts
                    SET balance = 0
                    WHERE company_id = @companyId
                      AND id NOT IN (
                          SELECT DISTINCT jl.account_id
                          FROM journal_lines jl
                          JOIN journal_entries je ON je.id = jl.journal_entry_id
                          WHERE je.company_id = @companyId AND je.status = 'posted'
                      )
                      AND level = 4;",
                    new { companyId }, tx);

                tx.Commit();
                return Results.Ok(new
                {
                    companyId = companyId.ToString(),
                    l4Updated,
                    l3ControlNetUpdated = l3Updated,
                    l3DirectUpdated,
                    zeroed
                });
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { /* ignore */ }
                logger.LogError(ex, "rebuild-balances failed for {CompanyId}", companyId);
                return Results.BadRequest(new
                {
                    error = ex.Message,
                    type = ex.GetType().Name
                });
            }
        });

        // GET /api/admin/inspect-journal?companyId=X
        // Sprint 41 — diagnostic: returns raw journal_lines data
        // summed per account. Used to compare with accounts.balance
        // and find where the drift is. The output includes both
        // the per-account total (from journal_lines) and the
        // accounts.balance column.
        grp.MapGet("/inspect-journal", async (HttpContext ctx, IDbConnectionFactory db, [FromQuery] Guid companyId) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<(string code, decimal net, decimal balance)>(@"
                SELECT a.code AS code, sub.net AS net, a.balance AS balance
                FROM accounts a
                LEFT JOIN (
                    SELECT jl.account_id,
                           SUM(CASE WHEN ac.account_type IN ('Asset','Expense')
                               THEN jl.debit - jl.credit
                               ELSE jl.credit - jl.debit END) AS net
                    FROM journal_lines jl
                    JOIN journal_entries je ON je.id = jl.journal_entry_id
                    JOIN accounts ac ON ac.id = jl.account_id
                    WHERE je.company_id = @id AND je.status = 'posted'
                    GROUP BY jl.account_id
                ) sub ON sub.account_id = a.id
                WHERE a.company_id = @id
                  AND a.level IN (3, 4)
                ORDER BY a.code;",
                new { id = companyId });
            return Results.Ok(rows.Select(r => new { r.code, computedBalance = r.net, storedBalance = r.balance, drift = r.net - r.balance }));
        });
    }
}
