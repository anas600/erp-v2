using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 56 — Project Technical Report (التقرير الفني) service.
///
/// Two main responsibilities:
///   1. Read aggregated progress from contract_line_item_progress
///      (driven by FMB approvals from Sprint 55) + billings.
///   2. Allow the user to override per-line progress
///      (is_manual_override = true) for executive decisions.
///
/// Dapper positional-record materialization requires the field
/// order in the record to MATCH the column order in the SELECT
/// projection exactly (Sprint 55 lessons).
/// </summary>
public class ProjectProgressService
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<ProjectProgressService> _log;

    public ProjectProgressService(IDbConnectionFactory db, ILogger<ProjectProgressService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<ProjectProgressDto> GetProgressAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();

        var p = await conn.QuerySingleOrDefaultAsync<ProjectLite>(@"
            SELECT id, code, name, physical_progress_percent,
                   financial_progress_percent, schedule_status,
                   execution_status, tech_report_date
            FROM projects WHERE id = @projectId;",
            new { projectId });
        if (p is null)
            throw new InvalidOperationException("المشروع غير موجود");

        var lineItems = (await conn.QueryAsync<LineItemProgressRow>(@"
            SELECT p.id AS id, p.line_item_id AS line_item_id,
                   p.quantity_done AS quantity_done,
                   p.progress_percent AS progress_percent,
                   p.last_updated AS last_updated,
                   p.is_manual_override AS is_manual_override,
                   p.notes AS notes,
                   li.line_number AS line_number,
                   li.description AS description, li.unit AS unit,
                   li.quantity AS contract_quantity,
                   li.unit_price AS unit_price
            FROM contract_line_item_progress p
            JOIN contract_line_items li ON li.id = p.line_item_id
            WHERE p.project_id = @projectId
            ORDER BY li.line_number ASC;",
            new { projectId })).ToList();

        if (lineItems.Count == 0)
        {
            // No FMB-driven progress yet. Show all BOQ line items with
            // 0% progress so the user can edit them.
            // Cast the constants explicitly to decimal so Dapper sees
            // matching types for the record (otherwise Postgres infers
            // 0 as integer, which doesn't match decimal in the record).
            var allBoq = (await conn.QueryAsync<LineItemProgressRow>(@"
                SELECT gen_random_uuid() AS id,
                       li.id AS line_item_id,
                       0::decimal AS quantity_done,
                       0::decimal AS progress_percent,
                       NOW() AS last_updated,
                       false AS is_manual_override,
                       NULL AS notes,
                       li.line_number AS line_number,
                       li.description AS description, li.unit AS unit,
                       li.quantity AS contract_quantity,
                       li.unit_price AS unit_price
                FROM contract_line_items li
                WHERE li.contract_id = (
                    SELECT id FROM contracts WHERE project_id = @projectId LIMIT 1
                )
                ORDER BY li.line_number ASC;",
                new { projectId })).ToList();
            lineItems = allBoq;
        }

        var contractValue = await conn.QuerySingleOrDefaultAsync<decimal?>(@"
            SELECT contract_value FROM contracts WHERE project_id = @projectId LIMIT 1;",
            new { projectId });
        // Variations: add to contract value for the "effective contract"
        // Sum of approved variation items (total_price) across all variations
        // for this project's contract.
        var totalVariations = await conn.QuerySingleOrDefaultAsync<decimal?>(@"
            SELECT COALESCE(SUM(vi.total_price), 0)
            FROM contract_variation_items vi
            JOIN contract_variations cv ON cv.id = vi.variation_id
            JOIN contracts c ON c.id = cv.contract_id
            WHERE c.project_id = @projectId
              AND cv.status IN ('approved','APPROVED');",
            new { projectId });
        var effectiveContract = (contractValue ?? 0m) + (totalVariations ?? 0m);
        var totalBilled = await conn.QuerySingleOrDefaultAsync<decimal?>(@"
            SELECT COALESCE(SUM(gross_amount), 0) FROM progress_billings
            WHERE project_id = @projectId AND status = 'INVOICED';",
            new { projectId });
        // Calculate raw % first (can exceed 100% if billings > contract).
        // The DB-level raw % is the truth — we cap at 100 for display only.
        var financialPctRaw = effectiveContract > 0
            ? Math.Round((totalBilled ?? 0m) / effectiveContract * 100m, 2)
            : 0m;
        var financialPct = Math.Min(financialPctRaw, 100m);
        // When capped, surface a warning in the response via project notes.

        var itemDtos = lineItems.Select(li => new LineItemProgressDto(
            li.id, li.line_item_id,
            li.line_number, li.description, li.unit,
            li.contract_quantity, li.unit_price,
            li.quantity_done, li.progress_percent,
            Math.Round(li.quantity_done * li.unit_price, 3),
            li.last_updated, li.is_manual_override, li.notes
        )).ToList();

        var totalContract = itemDtos.Sum(x => x.ContractQuantity * x.UnitPrice);
        var totalCompleted = itemDtos.Sum(x => x.AmountCompleted);

        // Always recompute the physical % from the current state of
        // contract_line_item_progress. The stored column is only a
        // snapshot from the last manual line update — the FMB-driven
        // changes (Sprint 55) bypass that path. The weighted average
        // is the truth.
        var computedPhysicalPct = itemDtos.Count > 0
            ? Math.Round(
                itemDtos.Sum(x => x.ContractQuantity * x.UnitPrice * x.ProgressPercent) /
                Math.Max(totalContract, 0.0001m), 2)
            : 0m;
        var physicalPct = Math.Max(p.physical_progress_percent, computedPhysicalPct);

        return new ProjectProgressDto(
            p.id, p.code, p.name,
            physicalPct, financialPct,
            p.schedule_status ?? "on_track",
            p.execution_status ?? "in_progress",
            p.tech_report_date,
            itemDtos.Count,
            itemDtos.Count(x => x.ProgressPercent >= 100),
            totalContract, totalCompleted,
            itemDtos);
    }

    public async Task<ProjectProgressDto> UpdateHeaderAsync(Guid projectId, UpdateProjectProgressRequest req)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE projects
            SET physical_progress_percent = @physical,
                financial_progress_percent = @financial,
                schedule_status = @schedule,
                execution_status = @execution,
                tech_report_date = @techDate,
                updated_at = NOW()
            WHERE id = @id;",
            new
            {
                id = projectId,
                physical = req.PhysicalProgressPercent,
                financial = req.FinancialProgressPercent,
                schedule = req.ScheduleStatus,
                execution = req.ExecutionStatus,
                techDate = req.TechReportDate
            });
        if (rows == 0) throw new InvalidOperationException("المشروع غير موجود");
        return await GetProgressAsync(projectId);
    }

    public async Task<LineItemProgressDto> UpdateLineItemProgressAsync(
        Guid projectId, Guid lineItemId, UpdateLineItemProgressRequest req)
    {
        using var conn = _db.CreateConnection();
        var companyId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT company_id FROM projects WHERE id = @id;",
            new { id = projectId });
        if (companyId is null) throw new InvalidOperationException("المشروع غير موجود");

        await conn.ExecuteAsync(@"
            INSERT INTO contract_line_item_progress
                (id, company_id, line_item_id, project_id,
                 progress_percent, quantity_done, last_updated,
                 is_manual_override, notes)
            VALUES (gen_random_uuid(), @companyId, @lineItemId, @projectId,
                    @pct, @qty, NOW(),
                    @isOverride, @notes)
            ON CONFLICT (line_item_id) DO UPDATE SET
                progress_percent = EXCLUDED.progress_percent,
                quantity_done = EXCLUDED.quantity_done,
                last_updated = NOW(),
                is_manual_override = EXCLUDED.is_manual_override,
                notes = EXCLUDED.notes;",
            new
            {
                companyId,
                lineItemId,
                projectId,
                pct = req.ProgressPercent,
                qty = req.QuantityDone,
                isOverride = req.IsManualOverride,
                notes = req.Notes
            });

        var li = await conn.QuerySingleOrDefaultAsync<LineItemProgressRow>(@"
            SELECT p.id AS id, p.line_item_id AS line_item_id,
                   p.quantity_done AS quantity_done,
                   p.progress_percent AS progress_percent,
                   p.last_updated AS last_updated,
                   p.is_manual_override AS is_manual_override,
                   p.notes AS notes,
                   li.line_number AS line_number,
                   li.description AS description, li.unit AS unit,
                   li.quantity AS contract_quantity,
                   li.unit_price AS unit_price
            FROM contract_line_item_progress p
            JOIN contract_line_items li ON li.id = p.line_item_id
            WHERE p.line_item_id = @lineItemId AND p.project_id = @projectId;",
            new { lineItemId, projectId });

        if (li is null) throw new InvalidOperationException("لم يتم العثور على البند");

        await RecomputeProjectPhysicalProgressAsync(projectId);

        return new LineItemProgressDto(
            li.id, li.line_item_id,
            li.line_number, li.description, li.unit,
            li.contract_quantity, li.unit_price,
            li.quantity_done, li.progress_percent,
            Math.Round(li.quantity_done * li.unit_price, 3),
            li.last_updated, li.is_manual_override, li.notes);
    }

    private async Task RecomputeProjectPhysicalProgressAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        // Weighted average of progress_percent by contract value (quantity × unit_price)
        var weightedPct = await conn.ExecuteScalarAsync<decimal>(@"
            WITH line_vals AS (
                SELECT (li.quantity * li.unit_price) AS value,
                       p.progress_percent AS pct
                FROM contract_line_item_progress p
                JOIN contract_line_items li ON li.id = p.line_item_id
                WHERE p.project_id = @projectId
            )
            SELECT CASE
                WHEN COALESCE(SUM(value), 0) = 0 THEN 0
                ELSE ROUND(SUM(value * pct) / SUM(value), 2)
            END
            FROM line_vals;",
            new { projectId });
        await conn.ExecuteAsync(@"
            UPDATE projects SET physical_progress_percent = @pct, updated_at = NOW()
            WHERE id = @id;",
            new { pct = weightedPct, id = projectId });
    }

    // ============================================================
    // Dapper records — order MUST match the SELECT projection
    // ============================================================

    private record ProjectLite(
        Guid id, string code, string name,
        decimal physical_progress_percent, decimal financial_progress_percent,
        string? schedule_status, string? execution_status,
        DateTime? tech_report_date);

    // Order matches the SELECT projection exactly
    private record LineItemProgressRow(
        Guid id, Guid line_item_id,
        decimal quantity_done, decimal progress_percent,
        DateTime last_updated, bool is_manual_override, string? notes,
        int line_number, string description, string unit,
        decimal contract_quantity, decimal unit_price);
}
