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
    }
}
