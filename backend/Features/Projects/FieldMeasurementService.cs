using System.Text.Json;
using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 55 — Field Measurement Book (الدفتر الفني) service.
///
/// Lifecycle:
///   DRAFT → SUBMITTED (engineer submits) → APPROVED (consultant signs)
///   → CANCELLED (any time before APPROVED)
///
/// On APPROVE, we auto-update `contract_line_item_progress` for each
/// entry (quantity_done + progress_percent). This feeds the Project
/// Progress report (Sprint 56).
/// </summary>
public class FieldMeasurementService
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<FieldMeasurementService> _log;

    public FieldMeasurementService(IDbConnectionFactory db, ILogger<FieldMeasurementService> log)
    {
        _db = db;
        _log = log;
    }

    // ============================================================
    // CRUD on books
    // ============================================================

    public async Task<List<FieldMeasurementBookDto>> ListByProjectAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var headers = (await conn.QueryAsync<FmbHeaderRow>(@"
            SELECT id, company_id, project_id, contract_id, book_number,
                   measurement_date, measurement_period_from, measurement_period_to,
                   engineer_user_id, engineer_name,
                   consultant_user_id, consultant_name,
                   status, approved_at, notes, created_at, updated_at
            FROM field_measurement_books
            WHERE project_id = @projectId
            ORDER BY measurement_date DESC, created_at DESC;",
            new { projectId })).ToList();

        if (headers.Count == 0) return new List<FieldMeasurementBookDto>();

        var ids = headers.Select(h => h.id).ToList();
        var entries = (await conn.QueryAsync<FmbEntryRow>(@"
            SELECT e.id AS id, e.company_id AS company_id, e.fmb_id AS fmb_id, e.line_item_id AS line_item_id,
                   e.measurements AS measurements,
                   e.initial_total AS initial_total, e.deductions_total AS deductions_total, e.final_total AS final_total,
                   e.unit_price AS unit_price, e.amount AS amount, e.notes AS notes,
                   e.created_at AS created_at, e.updated_at AS updated_at,
                   li.line_number AS line_number, li.description AS description, li.unit AS unit
            FROM field_measurement_entries e
            JOIN contract_line_items li ON li.id = e.line_item_id
            WHERE fmb_id = ANY(@ids);",
            new { ids = ids.ToArray() })).ToList();

        return headers.Select(h => MapHeader(h, entries.Where(e => e.fmb_id == h.id).ToList())).ToList();
    }

    public async Task<FieldMeasurementBookDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var header = await conn.QuerySingleOrDefaultAsync<FmbHeaderRow>(@"
            SELECT id, company_id, project_id, contract_id, book_number,
                   measurement_date, measurement_period_from, measurement_period_to,
                   engineer_user_id, engineer_name,
                   consultant_user_id, consultant_name,
                   status, approved_at, notes, created_at, updated_at
            FROM field_measurement_books
            WHERE id = @id;",
            new { id });
        if (header is null) return null;

        var entries = (await conn.QueryAsync<FmbEntryRow>(@"
            SELECT e.id AS id, e.company_id AS company_id, e.fmb_id AS fmb_id, e.line_item_id AS line_item_id,
                   e.measurements AS measurements,
                   e.initial_total AS initial_total, e.deductions_total AS deductions_total, e.final_total AS final_total,
                   e.unit_price AS unit_price, e.amount AS amount, e.notes AS notes,
                   e.created_at AS created_at, e.updated_at AS updated_at,
                   li.line_number AS line_number, li.description AS description, li.unit AS unit
            FROM field_measurement_entries e
            JOIN contract_line_items li ON li.id = e.line_item_id
            WHERE fmb_id = @id
            ORDER BY created_at ASC;",
            new { id })).ToList();
        return MapHeader(header, entries);
    }

    public async Task<FieldMeasurementBookDto> CreateAsync(Guid projectId, CreateFieldMeasurementBookRequest req)
    {
        using var conn = _db.CreateConnection();
        // Verify project exists + get its company_id
        var proj = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id)?>(@"
            SELECT id, company_id FROM projects WHERE id = @id;",
            new { id = projectId });
        if (proj is null) throw new InvalidOperationException("المشروع غير موجود");

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO field_measurement_books (
                id, company_id, project_id, contract_id, book_number,
                measurement_date, measurement_period_from, measurement_period_to,
                engineer_user_id, engineer_name,
                consultant_user_id, consultant_name,
                status, notes, created_at
            ) VALUES (
                @id, @companyId, @projectId, @contractId, @bookNumber,
                @measurementDate, @periodFrom, @periodTo,
                @engineerUserId, @engineerName,
                @consultantUserId, @consultantName,
                'DRAFT', @notes, NOW()
            );",
            new
            {
                id,
                companyId = proj.Value.company_id,
                projectId,
                contractId = req.ContractId,
                bookNumber = req.BookNumber,
                measurementDate = req.MeasurementDate,
                periodFrom = req.PeriodFrom,
                periodTo = req.PeriodTo,
                engineerUserId = req.EngineerUserId,
                engineerName = req.EngineerName,
                consultantUserId = req.ConsultantUserId,
                consultantName = req.ConsultantName,
                notes = req.Notes
            });

        return (await GetByIdAsync(id))!;
    }

    public async Task<FieldMeasurementBookDto?> UpdateAsync(Guid id, UpdateFieldMeasurementBookRequest req)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE field_measurement_books
            SET book_number = @bookNumber,
                measurement_date = @measurementDate,
                measurement_period_from = @periodFrom,
                measurement_period_to = @periodTo,
                engineer_user_id = @engineerUserId,
                engineer_name = @engineerName,
                consultant_user_id = @consultantUserId,
                consultant_name = @consultantName,
                notes = @notes,
                updated_at = NOW()
            WHERE id = @id AND status = 'DRAFT';",
            new
            {
                id,
                bookNumber = req.BookNumber,
                measurementDate = req.MeasurementDate,
                periodFrom = req.PeriodFrom,
                periodTo = req.PeriodTo,
                engineerUserId = req.EngineerUserId,
                engineerName = req.EngineerName,
                consultantUserId = req.ConsultantUserId,
                consultantName = req.ConsultantName,
                notes = req.Notes
            });
        return rows == 0 ? null : await GetByIdAsync(id);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "DELETE FROM field_measurement_books WHERE id = @id AND status = 'DRAFT';",
            new { id });
        return rows > 0;
    }

    // ============================================================
    // Entries
    // ============================================================

    public async Task<FieldMeasurementEntryDto> AddEntryAsync(Guid fmbId, CreateFieldMeasurementEntryRequest req)
    {
        using var conn = _db.CreateConnection();
        // Verify the FMB is editable
        var fmbStatus = await conn.QuerySingleOrDefaultAsync<string?>(@"
            SELECT status FROM field_measurement_books WHERE id = @id;",
            new { id = fmbId });
        if (fmbStatus is null) throw new InvalidOperationException("الدفتر غير موجود");
        if (fmbStatus != "DRAFT")
            throw new InvalidOperationException("لا يمكن تعديل دفتر في حالة " + fmbStatus);

        // Load line item for unit_price + project/contract context
        var li = await conn.QuerySingleOrDefaultAsync<LineItemLite>(@"
            SELECT id, contract_id, line_number, description, unit, unit_price
            FROM contract_line_items WHERE id = @id;",
            new { id = req.LineItemId });
        if (li is null) throw new InvalidOperationException("بند العقد غير موجود");

        // Compute totals
        var (initial, deductions, final, amount) = ComputeTotals(
            req.Measurements, li.unit_price);

        var id = Guid.NewGuid();
        var measurementsJson = JsonSerializer.Serialize(req.Measurements);
        var companyId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT company_id FROM field_measurement_books WHERE id = @id;",
            new { id = fmbId });

        await conn.ExecuteAsync(@"
            INSERT INTO field_measurement_entries (
                id, company_id, fmb_id, line_item_id, measurements,
                initial_total, deductions_total, final_total,
                unit_price, amount, notes, created_at
            ) VALUES (
                @id, @companyId, @fmbId, @lineItemId, @measurements::jsonb,
                @initial, @deductions, @final,
                @unitPrice, @amount, @notes, NOW()
            );",
            new
            {
                id,
                companyId,
                fmbId,
                lineItemId = req.LineItemId,
                measurements = measurementsJson,
                initial,
                deductions,
                final,
                unitPrice = li.unit_price,
                amount,
                notes = req.Notes
            });

        return new FieldMeasurementEntryDto(
            id, fmbId, req.LineItemId,
            li.line_number, li.description, li.unit,
            req.Measurements, initial, deductions, final,
            li.unit_price, amount, req.Notes);
    }

    public async Task<FieldMeasurementEntryDto?> UpdateEntryAsync(Guid entryId, UpdateFieldMeasurementEntryRequest req)
    {
        using var conn = _db.CreateConnection();
        // Verify the parent FMB is editable
        var fmbId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT fmb_id FROM field_measurement_entries WHERE id = @id;",
            new { id = entryId });
        if (fmbId is null) return null;

        var fmbStatus = await conn.QuerySingleOrDefaultAsync<string>(@"
            SELECT status FROM field_measurement_books WHERE id = @id;",
            new { id = fmbId });
        if (fmbStatus != "DRAFT")
            throw new InvalidOperationException("لا يمكن تعديل دفتر في حالة " + fmbStatus);

        var li = await conn.QuerySingleOrDefaultAsync<LineItemLite>(@"
            SELECT li.id, li.contract_id, li.line_number, li.description, li.unit, li.unit_price
            FROM field_measurement_entries e
            JOIN contract_line_items li ON li.id = e.line_item_id
            WHERE e.id = @id;",
            new { id = entryId });
        if (li is null) return null;

        var (initial, deductions, final, amount) = ComputeTotals(
            req.Measurements, li.unit_price);
        var measurementsJson = JsonSerializer.Serialize(req.Measurements);

        await conn.ExecuteAsync(@"
            UPDATE field_measurement_entries
            SET measurements = @measurements::jsonb,
                initial_total = @initial, deductions_total = @deductions,
                final_total = @final, amount = @amount,
                notes = @notes, updated_at = NOW()
            WHERE id = @id;",
            new
            {
                id = entryId,
                measurements = measurementsJson,
                initial, deductions, final, amount,
                notes = req.Notes
            });

        return new FieldMeasurementEntryDto(
            entryId, fmbId.Value, li.id,
            li.line_number, li.description, li.unit,
            req.Measurements, initial, deductions, final,
            li.unit_price, amount, req.Notes);
    }

    public async Task<bool> DeleteEntryAsync(Guid entryId)
    {
        using var conn = _db.CreateConnection();
        var fmbId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT fmb_id FROM field_measurement_entries WHERE id = @id;",
            new { id = entryId });
        if (fmbId is null) return false;
        var fmbStatus = await conn.QuerySingleOrDefaultAsync<string>(@"
            SELECT status FROM field_measurement_books WHERE id = @id;",
            new { id = fmbId });
        if (fmbStatus != "DRAFT")
            throw new InvalidOperationException("لا يمكن تعديل دفتر في حالة " + fmbStatus);
        var rows = await conn.ExecuteAsync(
            "DELETE FROM field_measurement_entries WHERE id = @id;",
            new { id = entryId });
        return rows > 0;
    }

    // ============================================================
    // Lifecycle: submit + approve + reject
    // ============================================================

    public async Task<FieldMeasurementBookDto> SubmitAsync(Guid id, string? comments)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE field_measurement_books
            SET status = 'SUBMITTED', notes = COALESCE(@notes, notes), updated_at = NOW()
            WHERE id = @id AND status = 'DRAFT';",
            new { id, notes = comments });
        if (rows == 0)
            throw new InvalidOperationException("لا يمكن تقديم دفتر غير في حالة DRAFT");
        return (await GetByIdAsync(id))!;
    }

    public async Task<FieldMeasurementBookDto> ApproveAsync(Guid id, string? comments)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // 1) Approve the FMB
            var rows = await conn.ExecuteAsync(@"
                UPDATE field_measurement_books
                SET status = 'APPROVED', approved_at = NOW(),
                    notes = COALESCE(@notes, notes), updated_at = NOW()
                WHERE id = @id AND status = 'SUBMITTED';",
                new { id, notes = comments }, tx);
            if (rows == 0)
                throw new InvalidOperationException("لا يمكن اعتماد دفتر غير في حالة SUBMITTED");

            // 2) Auto-update contract_line_item_progress for each entry
            var entries = (await conn.QueryAsync<(Guid line_item_id, Guid project_id, Guid company_id, decimal final_total, decimal unit_price)>(@"
                SELECT e.line_item_id, b.project_id, b.company_id, e.final_total, e.unit_price
                FROM field_measurement_entries e
                JOIN field_measurement_books b ON b.id = e.fmb_id
                WHERE e.fmb_id = @id;",
                new { id }, tx)).ToList();

            foreach (var e in entries)
            {
                // Compute progress %: final_total / contract_quantity (from line item)
                var contractQty = await conn.QuerySingleOrDefaultAsync<decimal?>(@"
                    SELECT quantity FROM contract_line_items WHERE id = @id;",
                    new { id = e.line_item_id }, tx);
                if (contractQty is null || contractQty.Value == 0) continue;

                var pct = Math.Round((e.final_total / contractQty.Value) * 100m, 2);
                await conn.ExecuteAsync(@"
                    INSERT INTO contract_line_item_progress
                        (id, company_id, line_item_id, project_id,
                         progress_percent, quantity_done, last_updated, notes)
                    VALUES (gen_random_uuid(), @companyId, @lineItemId, @projectId,
                            @pct, @qty, NOW(), 'FMB approved')
                    ON CONFLICT (line_item_id) DO UPDATE SET
                        progress_percent = EXCLUDED.progress_percent,
                        quantity_done = EXCLUDED.quantity_done,
                        last_updated = NOW();",
                    new
                    {
                        companyId = e.company_id,
                        lineItemId = e.line_item_id,
                        projectId = e.project_id,
                        pct,
                        qty = e.final_total
                    }, tx);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return (await GetByIdAsync(id))!;
    }

    public async Task<FieldMeasurementBookDto> RejectAsync(Guid id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("سبب الرفض مطلوب");
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE field_measurement_books
            SET status = 'CANCELLED', notes = @reason, updated_at = NOW()
            WHERE id = @id AND status = 'SUBMITTED';",
            new { id, reason });
        if (rows == 0)
            throw new InvalidOperationException("لا يمكن رفض دفتر غير في حالة SUBMITTED");
        return (await GetByIdAsync(id))!;
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static (decimal initial, decimal deductions, decimal final, decimal amount)
        ComputeTotals(List<MeasurementRow> measurements, decimal unitPrice)
    {
        decimal initial = 0m;
        decimal deductions = 0m;
        foreach (var m in measurements)
        {
            if (m.Deduction.HasValue && m.Deduction.Value > 0)
            {
                deductions += m.Deduction.Value;
            }
            else if (m.InitialQty.HasValue)
            {
                // Prefer the explicit InitialQty if provided, else
                // compute from count × length × width × height.
                initial += m.InitialQty.Value;
            }
            else if (m.Count.HasValue)
            {
                var l = m.Length ?? 0m;
                var w = m.Width ?? 0m;
                var h = m.Height ?? 0m;
                initial += m.Count.Value * l * w * h;
            }
        }
        var final = Math.Max(0m, Math.Round(initial - deductions, 3));
        var amount = Math.Round(final * unitPrice, 3);
        return (Math.Round(initial, 3), Math.Round(deductions, 3), final, amount);
    }

    private static FieldMeasurementBookDto MapHeader(FmbHeaderRow h, List<FmbEntryRow> entries) =>
        new(
            h.id, h.company_id, h.project_id, h.contract_id, h.book_number,
            h.measurement_date, h.measurement_period_from, h.measurement_period_to,
            h.engineer_user_id, h.engineer_name,
            h.consultant_user_id, h.consultant_name,
            h.status, h.approved_at, h.notes, h.created_at, h.updated_at,
            entries.Select(e => new FieldMeasurementEntryDto(
                e.id, e.fmb_id, e.line_item_id,
                e.line_number, e.description, e.unit,
                DeserializeMeasurements(e.measurements),
                e.initial_total, e.deductions_total, e.final_total,
                e.unit_price, e.amount, e.notes
            )).ToList()
        );

    private static List<MeasurementRow> DeserializeMeasurements(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? new List<MeasurementRow>()
            : (JsonSerializer.Deserialize<List<MeasurementRow>>(json) ?? new List<MeasurementRow>());

    private record FmbHeaderRow(
        Guid id, Guid company_id, Guid project_id, Guid? contract_id,
        string book_number, DateTime measurement_date,
        DateTime? measurement_period_from, DateTime? measurement_period_to,
        Guid? engineer_user_id, string? engineer_name,
        Guid? consultant_user_id, string? consultant_name,
        string status, DateTime? approved_at, string? notes,
        DateTime created_at, DateTime? updated_at);

    private record FmbEntryRow(
        Guid id, Guid company_id, Guid fmb_id, Guid line_item_id,
        string measurements, decimal initial_total, decimal deductions_total,
        decimal final_total, decimal unit_price, decimal amount,
        string? notes, DateTime created_at, DateTime? updated_at,
        int line_number, string description, string unit);

    private record LineItemLite(
        Guid id, Guid contract_id, int line_number, string description,
        string unit, decimal unit_price);
}
