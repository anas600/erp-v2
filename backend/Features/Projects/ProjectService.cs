using Dapper;
using ErpV2.Common;
using ErpV2.Features.Rules;
using ErpV2.Features.Journal;

namespace ErpV2.Features.Projects;

/// <summary>
/// Project service: manages projects and their milestones.
/// Completing a milestone triggers the "ProjectMilestoneCompleted" event
/// in the rules engine, which (with the default template) creates a journal entry.
///
/// Sprint 35 — extended with:
///   * project type, customer, contract, manager, location
///   * P&amp;L reporting (revenue vs costs on 5401-5407)
///   * bulk cost allocation (tag invoices / JEs with a project)
///   * company-wide P&amp;L report
/// </summary>
public class ProjectService
{
    private readonly IDbConnectionFactory _db;
    private readonly RuleEvaluator _rules;
    private readonly ProjectCostAccountService _costAccounts;
    private readonly ILogger<ProjectService>? _log;

    public ProjectService(
        IDbConnectionFactory db,
        RuleEvaluator rules,
        ProjectCostAccountService costAccounts,
        ILogger<ProjectService>? log = null)
    {
        _db = db;
        _rules = rules;
        _costAccounts = costAccounts;
        _log = log;
    }

    public async Task<List<ProjectDto>> GetByCompanyAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var projectIds = (await conn.QueryAsync<Guid>(@"
            SELECT id FROM projects
            WHERE company_id = @companyId
            ORDER BY created_at DESC;",
            new { companyId })).ToList();

        var result = new List<ProjectDto>();
        foreach (var id in projectIds)
        {
            var p = await GetByIdAsync(id);
            if (p is not null) result.Add(p);
        }
        return result;
    }

    public async Task<ProjectDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var p = await conn.QuerySingleOrDefaultAsync<ProjectRow>(@"
            SELECT id, company_id, code, name, name_ar, description, status,
                   start_date, end_date, budget, actual_cost, notes, created_at, updated_at,
                   type, customer_id, contract_value, expected_end_date, actual_end_date,
                   project_manager, location, contractor_id, consultant_id, physical_progress_percent, financial_progress_percent, schedule_status, execution_status, tech_report_date
            FROM projects WHERE id = @id;",
            new { id });
        if (p is null) return null;

        var milestones = (await conn.QueryAsync<MilestoneRow>(@"
            SELECT id, project_id, name, name_ar, description, amount, status,
                   target_date, completed_at, order_index
            FROM project_milestones
            WHERE project_id = @id
            ORDER BY order_index;",
            new { id })).ToList();

        // Sprint 35 — denormalized customer name. Optional JOIN: a
        // project may not have a customer_id (e.g. internal R&D).
        string? customerName = null;
        if (p.customer_id.HasValue)
        {
            customerName = await conn.QuerySingleOrDefaultAsync<string?>(@"
                SELECT name FROM contacts WHERE id = @id;",
                new { id = p.customer_id.Value });
        }

        // Sprint 54 — 4-party model: load contractor + consultant names
        string? contractorName = null;
        if (p.contractor_id.HasValue)
        {
            contractorName = await conn.QuerySingleOrDefaultAsync<string?>(@"
                SELECT name FROM contacts WHERE id = @id;",
                new { id = p.contractor_id.Value });
        }
        string? consultantName = null;
        if (p.consultant_id.HasValue)
        {
            consultantName = await conn.QuerySingleOrDefaultAsync<string?>(@"
                SELECT name FROM contacts WHERE id = @id;",
                new { id = p.consultant_id.Value });
        }

        return new ProjectDto(
            p.id, p.company_id, p.code, p.name, p.name_ar, p.description, p.status,
            p.start_date, p.end_date, p.budget, p.actual_cost, p.notes, p.created_at, p.updated_at,
            milestones.Select(m => new MilestoneDto(
                m.id, m.project_id, m.name, m.name_ar, m.description, m.amount,
                m.status, m.target_date, m.completed_at, m.order_index
            )).ToList(),
            // Sprint 35 fields
            p.type, p.customer_id, customerName, p.contract_value ?? 0m,
            p.expected_end_date, p.actual_end_date, p.project_manager, p.location,
            // Sprint 54 fields — 4-party model
            p.contractor_id, contractorName, p.consultant_id, consultantName,
            // Sprint 56 fields — Technical Report
            p.physical_progress_percent, p.financial_progress_percent,
            p.schedule_status, p.execution_status, p.tech_report_date
        );
    }

    public async Task<ProjectDto> CreateAsync(CreateProjectRequest req)
    {
        using var conn = _db.CreateConnection();
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO projects (id, company_id, code, name, name_ar, description,
                status, start_date, end_date, budget, notes,
                type, customer_id, contract_value, expected_end_date,
                project_manager, location,
                contractor_id, consultant_id)
            VALUES (@id, @companyId, @code, @name, @nameAr, @description,
                'draft', @startDate, @endDate, @budget, @notes,
                @type, @customerId, @contractValue, @expectedEndDate,
                @projectManager, @location,
                @contractorId, @consultantId, @physicalProgressPercent, @financialProgressPercent, @scheduleStatus, @executionStatus, @techReportDate);",
            new
            {
                id,
                companyId = req.CompanyId,
                code = req.Code,
                name = req.Name,
                nameAr = req.NameAr,
                description = req.Description,
                startDate = req.StartDate,
                endDate = req.EndDate,
                budget = req.Budget,
                notes = req.Notes,
                // Sprint 35
                type = req.Type,
                customerId = req.CustomerId,
                contractValue = req.ContractValue,
                expectedEndDate = req.ExpectedEndDate,
                projectManager = req.ProjectManager,
                location = req.Location,
                // Sprint 54 — 4-party model
                contractorId = req.ContractorId,
                consultantId = req.ConsultantId,
                // Sprint 56 — Technical Report
                physicalProgressPercent = req.PhysicalProgressPercent,
                financialProgressPercent = req.FinancialProgressPercent,
                scheduleStatus = req.ScheduleStatus,
                executionStatus = req.ExecutionStatus,
                techReportDate = req.TechReportDate
            });

        // Sprint 50 — auto-create the 7 L4 sub-ledger accounts for this
        // project's cost tracking. Idempotent: safe to call on every
        // create. This must run AFTER the project row exists so the
        // sub-ledger code can include the project code.
        try
        {
            await _costAccounts.CreateProjectSubLedgersAsync(id);
        }
        catch (Exception ex)
        {
            // Don't fail the project creation if sub-ledger creation
            // fails (e.g. COA missing 5401-5407 in a custom company
            // chart). The user can run it later via a separate admin
            // command. We log the issue for visibility.
            _log?.LogWarning(
                "Project {ProjectId} created but sub-ledger auto-create failed: {Msg}",
                id, ex.Message);
        }

        return (await GetByIdAsync(id))!;
    }

    public async Task<ProjectDto?> UpdateAsync(Guid id, UpdateProjectRequest req)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE projects
            SET name = @name, name_ar = @nameAr, description = @description,
                status = @status, start_date = @startDate, end_date = @endDate,
                budget = @budget, notes = @notes, updated_at = NOW(),
                type = @type, customer_id = @customerId,
                contract_value = @contractValue, expected_end_date = @expectedEndDate,
                actual_end_date = @actualEndDate, project_manager = @projectManager,
                location = @location,
                contractor_id = @contractorId, consultant_id = @consultantId, physical_progress_percent = @physicalProgressPercent, financial_progress_percent = @financialProgressPercent, schedule_status = @scheduleStatus, execution_status = @executionStatus, tech_report_date = @techReportDate
            WHERE id = @id;",
            new
            {
                id,
                name = req.Name,
                nameAr = req.NameAr,
                description = req.Description,
                status = req.Status,
                startDate = req.StartDate,
                endDate = req.EndDate,
                budget = req.Budget,
                notes = req.Notes,
                // Sprint 35
                type = req.Type,
                customerId = req.CustomerId,
                contractValue = req.ContractValue,
                expectedEndDate = req.ExpectedEndDate,
                actualEndDate = req.ActualEndDate,
                projectManager = req.ProjectManager,
                location = req.Location,
                // Sprint 54 — 4-party model
                contractorId = req.ContractorId,
                consultantId = req.ConsultantId,
                // Sprint 56 — Technical Report
                physicalProgressPercent = req.PhysicalProgressPercent,
                financialProgressPercent = req.FinancialProgressPercent,
                scheduleStatus = req.ScheduleStatus,
                executionStatus = req.ExecutionStatus,
                techReportDate = req.TechReportDate
            });
        return rows == 0 ? null : await GetByIdAsync(id);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "DELETE FROM projects WHERE id = @id;",
            new { id });
        return rows > 0;
    }

    public async Task<MilestoneDto> AddMilestoneAsync(Guid projectId, CreateMilestoneRequest req)
    {
        using var conn = _db.CreateConnection();
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO project_milestones (id, project_id, name, name_ar, description, amount, target_date, order_index)
            VALUES (@id, @projectId, @name, @nameAr, @description, @amount, @targetDate, @orderIndex);",
            new
            {
                id,
                projectId,
                name = req.Name,
                nameAr = req.NameAr,
                description = req.Description,
                amount = req.Amount,
                targetDate = req.TargetDate,
                orderIndex = req.OrderIndex
            });

        return (await conn.QuerySingleAsync<MilestoneRow>(@"
            SELECT id, project_id, name, name_ar, description, amount, status,
                   target_date, completed_at, order_index
            FROM project_milestones WHERE id = @id;",
            new { id })) is var row
            ? new MilestoneDto(row.id, row.project_id, row.name, row.name_ar, row.description,
                row.amount, row.status, row.target_date, row.completed_at, row.order_index)
            : throw new InvalidOperationException("Failed to load inserted milestone");
    }

    /// <summary>
    /// Completes a milestone: marks it as completed, then dispatches a
    /// "ProjectMilestoneCompleted" event to the rules engine. Returns the
    /// list of journal entries created by any matching rules.
    /// </summary>
    public async Task<List<JournalEntryDto>> CompleteMilestoneAsync(
        Guid projectId, Guid milestoneId, Guid? userId)
    {
        using (var conn = _db.CreateConnection())
        {
            var project = await GetByIdAsync(projectId)
                ?? throw new InvalidOperationException("المشروع غير موجود");
            var milestone = project.Milestones.FirstOrDefault(m => m.Id == milestoneId)
                ?? throw new InvalidOperationException("المرحلة غير موجودة");
            if (milestone.Status == "completed")
                throw new InvalidOperationException("المرحلة مكتملة بالفعل");

            await conn.ExecuteAsync(@"
                UPDATE project_milestones
                SET status = 'completed', completed_at = NOW()
                WHERE id = @id;",
                new { id = milestoneId });

            // Update project actual cost
            await conn.ExecuteAsync(@"
                UPDATE projects SET actual_cost = actual_cost + @amount, updated_at = NOW()
                WHERE id = @id;",
                new { amount = milestone.Amount, id = projectId });

            // Dispatch event to rules engine
            return await _rules.TriggerEventAsync(projectId, userId, "ProjectMilestoneCompleted", new Dictionary<string, object>
            {
                ["project"] = new Dictionary<string, object>
                {
                    ["id"] = project.Id.ToString(),
                    ["name"] = project.Name,
                    ["nameAr"] = project.NameAr ?? project.Name
                },
                ["milestone"] = new Dictionary<string, object>
                {
                    ["id"] = milestone.Id.ToString(),
                    ["name"] = milestone.Name,
                    ["nameAr"] = milestone.NameAr ?? milestone.Name,
                    ["amount"] = milestone.Amount
                }
            });
        }
    }

    // ============================================================
    // Sprint 35 — Cost allocation + P&L
    // ============================================================

    /// <summary>
    /// Bulk-assigns the given invoices to this project. Idempotent:
    /// re-assigning the same invoice is a no-op. Refuses cross-company
    /// assignments (400 BadRequest with an Arabic message).
    ///
    /// Only sales OR purchase invoices can be allocated; the validator
    /// checks that the project.company_id matches each invoice's
    /// company_id. If any invoice belongs to a different company, the
    /// entire operation is rejected (no partial assignment).
    /// </summary>
    public async Task<int> AllocateInvoicesAsync(Guid projectId, List<Guid> invoiceIds)
    {
        if (invoiceIds is null || invoiceIds.Count == 0) return 0;

        using var conn = _db.CreateConnection();
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id)?>(@"
            SELECT id, company_id FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null) throw new InvalidOperationException("المشروع غير موجود");

        // Cross-company guard. Pre-check so the UPDATE is safe.
        var wrongCompany = await conn.QueryAsync<Guid>(@"
            SELECT id FROM invoices
            WHERE id = ANY(@ids) AND company_id <> @companyId;",
            new { ids = invoiceIds, companyId = project.Value.company_id });
        if (wrongCompany.Any())
        {
            throw new InvalidOperationException(
                "إحدى الفواتير لا تنتمي لنفس شركة المشروع. " +
                $"عدد الفواتير الخاطئة: {wrongCompany.Count()}");
        }

        // UPDATE ... WHERE id = ANY(@ids). Re-assignment is a no-op
        // because the value is the same — that's the idempotency.
        var rows = await conn.ExecuteAsync(@"
            UPDATE invoices
            SET project_id = @projectId
            WHERE id = ANY(@ids) AND company_id = @companyId;",
            new { projectId, ids = invoiceIds, companyId = project.Value.company_id });
        return rows;
    }

    /// <summary>
    /// Bulk-assigns the given journal entries to this project.
    /// Idempotent and cross-company safe (same pattern as invoices).
    /// </summary>
    public async Task<int> AllocateJournalEntriesAsync(Guid projectId, List<Guid> entryIds)
    {
        if (entryIds is null || entryIds.Count == 0) return 0;

        using var conn = _db.CreateConnection();
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id)?>(@"
            SELECT id, company_id FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null) throw new InvalidOperationException("المشروع غير موجود");

        var wrongCompany = await conn.QueryAsync<Guid>(@"
            SELECT id FROM journal_entries
            WHERE id = ANY(@ids) AND company_id <> @companyId;",
            new { ids = entryIds, companyId = project.Value.company_id });
        if (wrongCompany.Any())
        {
            throw new InvalidOperationException(
                "أحد القيود لا ينتمي لنفس شركة المشروع. " +
                $"عدد القيود الخاطئة: {wrongCompany.Count()}");
        }

        var rows = await conn.ExecuteAsync(@"
            UPDATE journal_entries
            SET project_id = @projectId
            WHERE id = ANY(@ids) AND company_id = @companyId;",
            new { projectId, ids = entryIds, companyId = project.Value.company_id });
        return rows;
    }

    /// <summary>
    /// De-allocate (clear project_id on) the given invoices. Same
    /// cross-company guard as the allocator — refusing to silently
    /// un-tag invoices that don't even belong to the project's
    /// company.
    /// </summary>
    public async Task<int> DeallocateInvoicesAsync(Guid projectId, List<Guid> invoiceIds)
    {
        if (invoiceIds is null || invoiceIds.Count == 0) return 0;

        using var conn = _db.CreateConnection();
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id)?>(@"
            SELECT id, company_id FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null) throw new InvalidOperationException("المشروع غير موجود");

        var wrongCompany = await conn.QueryAsync<Guid>(@"
            SELECT id FROM invoices
            WHERE id = ANY(@ids) AND company_id <> @companyId;",
            new { ids = invoiceIds, companyId = project.Value.company_id });
        if (wrongCompany.Any())
        {
            throw new InvalidOperationException(
                "إحدى الفواتير لا تنتمي لنفس شركة المشروع");
        }

        var rows = await conn.ExecuteAsync(@"
            UPDATE invoices
            SET project_id = NULL
            WHERE id = ANY(@ids) AND company_id = @companyId AND project_id = @projectId;",
            new { projectId, ids = invoiceIds, companyId = project.Value.company_id });
        return rows;
    }

    /// <summary>
    /// Returns the list of all invoices + JE lines tagged with this
    /// project. Used by the "Project Costs" page.
    /// </summary>
    public async Task<List<ProjectCostLine>> GetCostsAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var result = new List<ProjectCostLine>();

        // 1) Purchase invoices tagged with this project. We include
        //    both the invoice header (so the user can drill into
        //    it) and the line breakdown. Sales invoices are also
        //    returned here (as a "revenue" line) — we want a
        //    single unified view of all tagged activity.
        var invoiceRows = await conn.QueryAsync<InvoiceCostRow>(@"
            SELECT i.id AS invoice_id, i.invoice_number, i.invoice_type, i.invoice_date,
                   i.party_name, i.total, i.tax_amount, i.subtotal,
                   a.code AS account_code, a.name AS account_name,
                   il.description, il.amount AS line_amount
            FROM invoices i
            JOIN invoice_lines il ON il.invoice_id = i.id
            LEFT JOIN accounts a ON a.id = il.account_id
            WHERE i.project_id = @projectId
            ORDER BY i.invoice_date DESC, i.invoice_number;",
            new { projectId });
        foreach (var r in invoiceRows)
        {
            result.Add(new ProjectCostLine(
                Source: "invoice",
                Id: r.invoice_id,
                Date: r.invoice_date,
                Reference: r.invoice_number,
                Description: r.description ?? $"{r.invoice_type} - {r.party_name}",
                AccountCode: r.account_code,
                AccountName: r.account_name,
                Amount: r.line_amount,
                InvoiceType: r.invoice_type
            ));
        }

        // 2) Direct journal entries tagged with this project
        //    (manual entries, or any JE that the user manually
        //    allocated even if no invoice is involved). We pull
        //    every line on each tagged entry.
        var jeRows = await conn.QueryAsync<JournalCostRow>(@"
            SELECT je.id AS entry_id, je.entry_number, je.entry_date, je.narration,
                   jl.id AS line_id,
                   a.code AS account_code, a.name AS account_name,
                   jl.description AS line_description,
                   jl.debit, jl.credit
            FROM journal_entries je
            JOIN journal_lines jl ON jl.journal_entry_id = je.id
            LEFT JOIN accounts a ON a.id = jl.account_id
            WHERE je.project_id = @projectId
            ORDER BY je.entry_date DESC, je.entry_number;",
            new { projectId });
        foreach (var r in jeRows)
        {
            // Cost = the bigger of (debit, credit) — for an
            // expense line, debit > 0; for a payable line, credit
            // > 0. We display both columns and use the absolute
            // larger one for the "amount" field (so a single line
            // is informative without forcing the user to know
            // debit/credit semantics).
            var amount = r.debit > 0 ? r.debit : r.credit;
            result.Add(new ProjectCostLine(
                Source: "journal",
                Id: r.entry_id,
                Date: r.entry_date,
                Reference: r.entry_number,
                Description: r.line_description ?? r.narration ?? "",
                AccountCode: r.account_code,
                AccountName: r.account_name,
                Amount: amount,
                Debit: r.debit,
                Credit: r.credit
            ));
        }

        return result.OrderByDescending(x => x.Date).ToList();
    }

    /// <summary>
    /// Returns just the sales invoices tagged with this project —
    /// the "revenue" half of the P&amp;L. Returns full invoice
    /// snapshots (header + line breakdown).
    /// </summary>
    public async Task<List<ProjectRevenueLine>> GetRevenueAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<InvoiceRevenueRow>(@"
            SELECT i.id AS invoice_id, i.invoice_number, i.invoice_date,
                   i.party_name, i.subtotal, i.tax_amount, i.total,
                   i.status, i.invoice_type
            FROM invoices i
            WHERE i.project_id = @projectId
              AND i.invoice_type = 'sales'
              AND i.status = 'posted'
            ORDER BY i.invoice_date DESC, i.invoice_number;",
            new { projectId });

        return rows.Select(r => new ProjectRevenueLine(
            r.invoice_id, r.invoice_number, r.invoice_date, r.party_name,
            r.subtotal, r.tax_amount, r.total, r.status, r.invoice_type
        )).ToList();
    }

    /// <summary>
    /// P&amp;L for a single project. Revenue = sum of POSTED sales
    /// invoices tagged with the project. Costs = sum of journal
    /// lines on accounts 5401-5407, plus the lines of any purchase
    /// invoice tagged with the project (the invoice lines already
    /// land in the JE — we count it once via the JE to avoid
    /// double-counting).
    /// </summary>
    public async Task<ProjectPnLResponse?> GetPnLAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var project = await conn.QuerySingleOrDefaultAsync<ProjectLite>(@"
            SELECT id, company_id, code, name FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null) return null;

        // 1) Revenue — sum of POSTED sales invoices.
        var revenue = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(total), 0) FROM invoices
            WHERE project_id = @projectId
              AND invoice_type = 'sales'
              AND status = 'posted';",
            new { projectId }) ?? 0m;

        // 2) Costs — group journal lines on expense accounts
        //    (5401-5407) that are tagged with this project. The
        //    "amount" is the larger of (debit, credit) per line,
        //    which is the natural expense convention.
        //
        //    Why journal_entries and not invoice_lines directly?
        //    Because every posted purchase invoice produces a
        //    journal entry — counting both would double-count.
        //    The JE is the authoritative source of accounting
        //    facts; invoice lines are presentation only.
        var costRows = await conn.QueryAsync<CostGroupRow>(@"
            SELECT a.code AS account_code,
                   COALESCE(a.name, 'بدون اسم') AS account_name,
                   SUM(GREATEST(jl.debit, jl.credit)) AS amount
            FROM journal_entries je
            JOIN journal_lines jl ON jl.journal_entry_id = je.id
            JOIN accounts a ON a.id = jl.account_id
            WHERE je.project_id = @projectId
              AND a.code LIKE '54%'
            GROUP BY a.code, a.name
            ORDER BY a.code;",
            new { projectId });

        var costCategories = costRows.Select(r => new CostCategoryPnL(
            Category: CostCategoryLabel(r.account_code),
            AccountCode: r.account_code,
            Amount: r.amount
        )).ToList();
        var totalCosts = costCategories.Sum(c => c.Amount);

        // 3) Counts for the UI badge.
        var invoiceCount = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM invoices
            WHERE project_id = @projectId
              AND invoice_type = 'sales'
              AND status = 'posted';",
            new { projectId });
        var jeCount = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM journal_entries
            WHERE project_id = @projectId;",
            new { projectId });

        var grossProfit = revenue - totalCosts;
        // Profit margin: percent of revenue. Guard against div-by-zero.
        var margin = revenue > 0
            ? Math.Round((grossProfit / revenue) * 100, 2)
            : 0m;

        return new ProjectPnLResponse(
            ProjectId: project.id,
            ProjectCode: project.code,
            ProjectName: project.name,
            TotalRevenue: revenue,
            CostsByCategory: costCategories,
            TotalCosts: totalCosts,
            GrossProfit: grossProfit,
            ProfitMargin: margin,
            InvoiceCount: invoiceCount,
            JournalEntryCount: jeCount
        );
    }

    /// <summary>
    /// Company-wide P&amp;L: iterates all projects in the company
    /// and returns the P&amp;L for each. Projects with no activity
    /// are included with zeros (so the report shows "empty"
    /// projects too).
    /// </summary>
    public async Task<List<ProjectPnLResponse>> GetCompanyPnLAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var projectIds = (await conn.QueryAsync<Guid>(@"
            SELECT id FROM projects WHERE company_id = @companyId;",
            new { companyId })).ToList();

        var result = new List<ProjectPnLResponse>();
        foreach (var id in projectIds)
        {
            var pnl = await GetPnLAsync(id);
            if (pnl is not null) result.Add(pnl);
        }
        return result;
    }

    /// <summary>
    /// Map a 4-digit expense account code (5401-5407) to a
    /// human-readable Arabic label. Used by the P&amp;L UI so the
    /// user doesn't have to memorize COA codes.
    /// </summary>
    private static string CostCategoryLabel(string code) => code switch
    {
        "5401" => "مواد خام",
        "5402" => "أجور",
        "5403" => "إيجار",
        "5404" => "كهرباء ومياه",
        "5405" => "صيانة",
        "5406" => "مصاريف إدارية",
        "5407" => "مصاريف متنوعة",
        _      => $"حساب {code}"
    };

    private record ProjectRow(
        Guid id, Guid company_id, string code, string name, string? name_ar,
        string? description, string status, DateTime? start_date, DateTime? end_date,
        decimal budget, decimal actual_cost, string? notes,
        DateTime created_at, DateTime? updated_at,
        // Sprint 35
        string? type, Guid? customer_id, decimal? contract_value,
        DateTime? expected_end_date, DateTime? actual_end_date,
        string? project_manager, string? location,
        // Sprint 54 — 4-party project model
        Guid? contractor_id, Guid? consultant_id,
        // Sprint 56 — Technical Report
        decimal physical_progress_percent, decimal financial_progress_percent,
        string? schedule_status, string? execution_status,
        DateTime? tech_report_date);

    private record MilestoneRow(
        Guid id, Guid project_id, string name, string? name_ar, string? description,
        decimal amount, string status, DateTime? target_date, DateTime? completed_at, int order_index);

    private record ProjectLite(Guid id, Guid company_id, string code, string name);

    private record CostGroupRow(string account_code, string account_name, decimal amount);

    private record InvoiceCostRow(
        Guid invoice_id, string invoice_number, string invoice_type, DateTime invoice_date,
        string party_name, decimal total, decimal tax_amount, decimal subtotal,
        string? account_code, string? account_name, string? description, decimal line_amount);

    private record JournalCostRow(
        Guid entry_id, string entry_number, DateTime entry_date, string? narration,
        Guid line_id, string? account_code, string? account_name, string? line_description,
        decimal debit, decimal credit);

    private record InvoiceRevenueRow(
        Guid invoice_id, string invoice_number, DateTime invoice_date, string party_name,
        decimal subtotal, decimal tax_amount, decimal total, string status, string invoice_type);
}

/// <summary>
/// One line in the project costs list. Used by the GET
/// /api/projects/{id}/costs endpoint.
/// </summary>
public record ProjectCostLine(
    string Source,          // "invoice" | "journal"
    Guid Id,                // the invoice_id or entry_id
    DateTime Date,
    string Reference,       // invoice_number or entry_number
    string Description,
    string? AccountCode,
    string? AccountName,
    decimal Amount,
    // Optional debit/credit for journal lines (zero for invoices).
    decimal Debit = 0,
    decimal Credit = 0,
    string? InvoiceType = null
);

/// <summary>
/// One line in the project revenue list. Returned by GET
/// /api/projects/{id}/revenue.
/// </summary>
public record ProjectRevenueLine(
    Guid InvoiceId,
    string InvoiceNumber,
    DateTime InvoiceDate,
    string PartyName,
    decimal SubTotal,
    decimal TaxAmount,
    decimal Total,
    string Status,
    string InvoiceType
);
