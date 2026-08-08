using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 38 — Contract Line Item (BOQ) service.
///
/// <para>
/// Manages the line items on a contract (the Bill of Quantities /
/// جدول الكميات). Each item is a measurable unit of work — m3 of
/// concrete, m2 of plaster, ton of steel, or a <c>lump</c> for
/// items you can't easily measure. The contract_value is the sum
/// of item.total_price.
/// </para>
///
/// <para>
/// Two import paths are supported:
/// <list type="bullet">
///   <item>Excel — POST /api/contracts/{id}/line-items/import-excel</item>
///   <item>Clipboard — POST /api/contracts/{id}/line-items/import-clipboard</item>
/// </list>
/// Both produce the same <see cref="ImportLineItemsResult"/> shape.
/// </para>
///
/// <para>
/// Validations:
/// <list type="bullet">
///   <item>total_price = quantity * unit_price (server-computed)</item>
///   <item>Cannot delete a line item if any non-cancelled billing has used it</item>
///   <item>Cannot reduce quantity below what's already been billed</item>
/// </list>
/// </para>
/// </summary>
public class LineItemService
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<LineItemService> _log;

    // The set of unit values the UI / contract templates use. The
    // column itself is a varchar so the DB doesn't enforce this —
    // we do, in C#. Anything else is treated as 'other' and requires
    // custom_unit.
    public static readonly HashSet<string> AllowedUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "m3", "m2", "m", "ton", "kg", "piece", "lump", "hour", "day", "other"
    };

    public LineItemService(IDbConnectionFactory db, ILogger<LineItemService> log)
    {
        _db = db;
        _log = log;
    }

    // ============================================================
    // Read
    // ============================================================

    /// <summary>
    /// Lists all line items for a contract in line-number order.
    /// The derived fields (QuantityBilledSoFar, QuantityRemaining)
    /// are computed in a single SQL pass that joins to
    /// billing_line_items, so the UI can render the BOQ table
    /// with one round-trip.
    /// </summary>
    public async Task<List<ContractLineItemDto>> GetByContractAsync(Guid contractId)
    {
        using var conn = _db.CreateConnection();
        // SUM over billing_line_items is LEFT JOINed: items with no
        // claims yet get 0, not NULL. We exclude CANCELLED billings
        // because their claims don't count toward "billed so far".
        var rows = await conn.QueryAsync<LineItemRow>(@"
            SELECT li.id, li.company_id, li.contract_id, li.line_number,
                   li.description, li.unit, li.custom_unit,
                   li.quantity, li.unit_price, li.total_price,
                   li.notes, li.created_at, li.updated_at,
                   COALESCE(SUM(bli.quantity_cumulative), 0) AS quantity_billed
            FROM contract_line_items li
            LEFT JOIN billing_line_items bli ON bli.line_item_id = li.id
            LEFT JOIN progress_billings pb ON pb.id = bli.billing_id
                AND pb.status != 'CANCELLED'
            WHERE li.contract_id = @contractId
            GROUP BY li.id
            ORDER BY li.line_number ASC;",
            new { contractId });
        return rows.Select(MapRow).ToList();
    }

    /// <summary>
    /// Loads a single line item by id with its derived fields.
    /// Returns null if the id doesn't exist.
    /// </summary>
    public async Task<ContractLineItemDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<LineItemRow>(@"
            SELECT li.id, li.company_id, li.contract_id, li.line_number,
                   li.description, li.unit, li.custom_unit,
                   li.quantity, li.unit_price, li.total_price,
                   li.notes, li.created_at, li.updated_at,
                   COALESCE((
                       SELECT SUM(bli.quantity_cumulative)
                       FROM billing_line_items bli
                       JOIN progress_billings pb ON pb.id = bli.billing_id
                       WHERE bli.line_item_id = li.id
                         AND pb.status != 'CANCELLED'
                   ), 0) AS quantity_billed
            FROM contract_line_items li
            WHERE li.id = @id;",
            new { id });
        return row is null ? null : MapRow(row);
    }

    // ============================================================
    // Create / Update / Delete
    // ============================================================

    /// <summary>
    /// Creates a new line item on a contract. The line number is
    /// auto-assigned (max + 1 within the contract). Refuses if:
    ///   - the contract doesn't exist
    ///   - the unit is not in <see cref="AllowedUnits"/> (and custom_unit is empty when unit='other')
    ///   - quantity or unit_price is negative
    /// </summary>
    public async Task<ContractLineItemDto> CreateAsync(Guid contractId, CreateLineItemRequest req)
    {
        ValidateLineItemInput(req.Description, req.Unit, req.CustomUnit, req.Quantity, req.UnitPrice);

        using var conn = _db.CreateConnection();
        var contract = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id)?>(@"
            SELECT id, company_id FROM contracts WHERE id = @id;",
            new { id = contractId });
        if (contract is null)
            throw new InvalidOperationException("العقد غير موجود");

        var nextLineNumber = await conn.ExecuteScalarAsync<int>(@"
            SELECT COALESCE(MAX(line_number), 0) + 1
            FROM contract_line_items WHERE contract_id = @contractId;",
            new { contractId });

        var id = Guid.NewGuid();
        var totalPrice = Math.Round(req.Quantity * req.UnitPrice, 3);
        await conn.ExecuteAsync(@"
            INSERT INTO contract_line_items (
                id, company_id, contract_id, line_number,
                description, unit, custom_unit,
                quantity, unit_price, total_price, notes, created_at
            )
            VALUES (
                @id, @companyId, @contractId, @lineNumber,
                @description, @unit, @customUnit,
                @quantity, @unitPrice, @totalPrice, @notes, NOW()
            );",
            new
            {
                id,
                companyId = contract.Value.company_id,
                contractId,
                lineNumber = nextLineNumber,
                description = req.Description,
                unit = req.Unit,
                customUnit = req.Unit == "other" ? req.CustomUnit : null,
                quantity = req.Quantity,
                unitPrice = req.UnitPrice,
                totalPrice,
                notes = req.Notes
            });

        return (await GetByIdAsync(id))!;
    }

    /// <summary>
    /// Updates a line item. Refuses if:
    ///   - the item doesn't exist
    ///   - the new quantity is less than what's already been billed
    ///     (you can't un-claim work the customer already paid for)
    ///   - validation rules (same as CreateAsync)
    /// </summary>
    public async Task<ContractLineItemDto?> UpdateAsync(Guid id, UpdateLineItemRequest req)
    {
        ValidateLineItemInput(req.Description, req.Unit, req.CustomUnit, req.Quantity, req.UnitPrice);

        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<LineItemRow>(@"
            SELECT id, company_id, contract_id, line_number, description, unit, custom_unit,
                   quantity, unit_price, total_price, notes, created_at, updated_at, 0 AS quantity_billed
            FROM contract_line_items WHERE id = @id;",
            new { id });
        if (existing is null) return null;

        // If the quantity is being reduced, refuse if we'd drop below
        // what's already been billed.
        var billed = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(bli.quantity_cumulative), 0)
            FROM billing_line_items bli
            JOIN progress_billings pb ON pb.id = bli.billing_id
            WHERE bli.line_item_id = @id
              AND pb.status != 'CANCELLED';",
            new { id }) ?? 0m;
        if (req.Quantity < billed)
            throw new InvalidOperationException(
                $"لا يمكن تقليل الكمية إلى {req.Quantity} — تم فوترة {billed} بالفعل على هذا البند.");

        var totalPrice = Math.Round(req.Quantity * req.UnitPrice, 3);
        await conn.ExecuteAsync(@"
            UPDATE contract_line_items
            SET description = @description,
                unit = @unit,
                custom_unit = @customUnit,
                quantity = @quantity,
                unit_price = @unitPrice,
                total_price = @totalPrice,
                notes = @notes,
                updated_at = NOW()
            WHERE id = @id;",
            new
            {
                id,
                description = req.Description,
                unit = req.Unit,
                customUnit = req.Unit == "other" ? req.CustomUnit : null,
                quantity = req.Quantity,
                unitPrice = req.UnitPrice,
                totalPrice,
                notes = req.Notes
            });

        return await GetByIdAsync(id);
    }

    /// <summary>
    /// Deletes a line item. Refuses if any non-cancelled billing
    /// has a claim against it — the claim would orphan, and the
    /// billing's gross/net would no longer be recomputable.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid contract_id)?>(@"
            SELECT id, contract_id FROM contract_line_items WHERE id = @id;",
            new { id });
        if (existing is null) return false;

        var used = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*)
            FROM billing_line_items bli
            JOIN progress_billings pb ON pb.id = bli.billing_id
            WHERE bli.line_item_id = @id
              AND pb.status != 'CANCELLED';",
            new { id });
        if (used > 0)
            throw new InvalidOperationException(
                "لا يمكن حذف بند تم فوترته بالفعل. الرجاء عكس المستخلصات المرتبطة أولاً.");

        var rows = await conn.ExecuteAsync(
            "DELETE FROM contract_line_items WHERE id = @id;",
            new { id });
        if (rows == 0) return false;

        // Re-pack line numbers so the remaining items are 1..N
        // contiguous. We do this in a single transaction so a
        // failure mid-pack doesn't leave gaps.
        await RepackLineNumbersAsync(existing.Value.contract_id);
        return true;
    }

    /// <summary>
    /// Reorders the line items on a contract. The list contains the
    /// ids in the desired display order; the service reassigns
    /// line_number = (index + 1). The whole reorder runs in a
    /// single transaction so a partial failure can't leave two
    /// items with the same number.
    /// </summary>
    public async Task<bool> ReorderAsync(Guid contractId, ReorderLineItemsRequest req)
    {
        if (req.LineItemIds is null || req.LineItemIds.Count == 0)
            throw new InvalidOperationException("قائمة البنود فارغة");

        // De-duplicate while preserving order.
        var ids = req.LineItemIds
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // Confirm all ids belong to this contract. If even one
            // doesn't, refuse the whole reorder (we don't want a
            // partial state).
            var matching = (await conn.QueryAsync<Guid>(@"
                SELECT id FROM contract_line_items
                WHERE contract_id = @contractId AND id = ANY(@ids);",
                new { contractId, ids = ids.ToArray() }, tx)).ToHashSet();
            if (matching.Count != ids.Count)
                throw new InvalidOperationException(
                    "بعض البنود في القائمة لا تنتمي لهذا العقد");

            // Walk the list, set line_number = position+1. We do
            // this with a temp offset to dodge the
            // UNIQUE(contract_id, line_number) constraint — if we
            // tried to set them in place, the first UPDATE would
            // collide with the existing row.
            const int TempOffset = 100000;
            for (int i = 0; i < ids.Count; i++)
            {
                await conn.ExecuteAsync(@"
                    UPDATE contract_line_items
                    SET line_number = @temp
                    WHERE id = @id;",
                    new { temp = TempOffset + i + 1, id = ids[i] }, tx);
            }
            for (int i = 0; i < ids.Count; i++)
            {
                await conn.ExecuteAsync(@"
                    UPDATE contract_line_items
                    SET line_number = @final
                    WHERE id = @id;",
                    new { final = i + 1, id = ids[i] }, tx);
            }
            // If there are line items on this contract that the
            // caller didn't include in the list (shouldn't happen
            // in normal use, but defend in depth), pack them after
            // the listed ones.
            var listed = ids.ToHashSet();
            var leftovers = (await conn.QueryAsync<Guid>(@"
                SELECT id FROM contract_line_items
                WHERE contract_id = @contractId AND id <> ALL(@ids)
                ORDER BY line_number ASC;",
                new { contractId, ids = ids.ToArray() }, tx)).ToList();
            var next = ids.Count + 1;
            foreach (var leftover in leftovers)
            {
                await conn.ExecuteAsync(@"
                    UPDATE contract_line_items
                    SET line_number = @temp
                    WHERE id = @id;",
                    new { temp = TempOffset + next, id = leftover }, tx);
                await conn.ExecuteAsync(@"
                    UPDATE contract_line_items
                    SET line_number = @final
                    WHERE id = @id;",
                    new { final = next, id = leftover }, tx);
                next++;
            }

            tx.Commit();
            return true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ============================================================
    // Import — Excel (ClosedXML) and clipboard (TSV)
    // ============================================================

    /// <summary>
    /// Parses an .xlsx file and inserts the rows as new line items
    /// on the contract. Expected columns (case-insensitive,
    /// order-flexible):
    /// <c>line_number, description, unit, quantity, unit_price</c>.
    /// The first row is treated as a header. Optional
    /// <c>custom_unit</c> column is supported.
    /// </summary>
    public async Task<ImportLineItemsResult> ImportFromExcelAsync(Guid contractId, ImportLineItemsRequest req)
    {
        var imported = new List<ImportedLineItem>();
        var errors = new List<string>();
        var totalRows = 0;

        if (req.Content is null || req.Content.Length == 0)
        {
            errors.Add("الملف فارغ");
            return new ImportLineItemsResult(0, 0, 1, imported, errors);
        }

        try
        {
            using var workbook = new XLWorkbook(new MemoryStream(req.Content));
            var sheet = workbook.Worksheet(1); // first sheet
            var rows = sheet.RangeUsed()?.RowsUsed().ToList();
            if (rows is null || rows.Count == 0)
            {
                errors.Add("الملف لا يحتوي على بيانات");
                return new ImportLineItemsResult(0, 0, 1, imported, errors);
            }

            // First row is the header — map column names to indices.
            var headerRow = rows[0];
            var headers = headerRow.Cells().ToList();
            var columnMap = MapHeaders(headers.Select(c => c.GetString().Trim()).ToList());

            if (!columnMap.ContainsKey("description") || !columnMap.ContainsKey("unit")
                || !columnMap.ContainsKey("quantity") || !columnMap.ContainsKey("unit_price"))
            {
                errors.Add("الأعمدة المطلوبة مفقودة: description, unit, quantity, unit_price");
                return new ImportLineItemsResult(0, 0, 1, imported, errors);
            }

            // Data rows start at index 1.
            for (int r = 1; r < rows.Count; r++)
            {
                totalRows++;
                var row = rows[r];
                var rowNum = r + 1; // human-friendly 1-based for error messages
                try
                {
                    var description = GetCellString(row.Cell(columnMap["description"] + 1));
                    var unit = GetCellString(row.Cell(columnMap["unit"] + 1)).ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(unit))
                    {
                        errors.Add($"صف {rowNum}: الوصف أو الوحدة فارغ");
                        continue;
                    }
                    var quantity = GetCellDecimal(row.Cell(columnMap["quantity"] + 1));
                    var unitPrice = GetCellDecimal(row.Cell(columnMap["unit_price"] + 1));
                    string? customUnit = null;
                    if (columnMap.TryGetValue("custom_unit", out var cuIdx))
                        customUnit = GetCellString(row.Cell(cuIdx + 1));
                    if (unit == "other" && string.IsNullOrWhiteSpace(customUnit))
                    {
                        errors.Add($"صف {rowNum}: custom_unit مطلوب عند unit=other");
                        continue;
                    }
                    var totalPrice = Math.Round(quantity * unitPrice, 3);
                    imported.Add(new ImportedLineItem(
                        LineNumber: totalRows,
                        Description: description,
                        Unit: unit,
                        CustomUnit: unit == "other" ? customUnit : null,
                        Quantity: quantity,
                        UnitPrice: unitPrice,
                        TotalPrice: totalPrice));
                }
                catch (Exception ex)
                {
                    errors.Add($"صف {rowNum}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Excel import failed for contract {ContractId}", contractId);
            errors.Add($"فشل قراءة الملف: {ex.Message}");
            return new ImportLineItemsResult(totalRows, imported.Count, errors.Count, imported, errors);
        }

        // Persist the successfully-parsed rows as new line items.
        using var conn = _db.CreateConnection();
        var contract = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id)?>(@"
            SELECT id, company_id FROM contracts WHERE id = @id;",
            new { id = contractId });
        if (contract is null)
        {
            errors.Add("العقد غير موجود");
            return new ImportLineItemsResult(totalRows, 0, errors.Count + 1, imported, errors);
        }
        var nextLineNumber = await conn.ExecuteScalarAsync<int>(@"
            SELECT COALESCE(MAX(line_number), 0)
            FROM contract_line_items WHERE contract_id = @contractId;",
            new { contractId });

        foreach (var item in imported)
        {
            nextLineNumber++;
            await conn.ExecuteAsync(@"
                INSERT INTO contract_line_items (
                    id, company_id, contract_id, line_number,
                    description, unit, custom_unit,
                    quantity, unit_price, total_price, created_at
                )
                VALUES (
                    @id, @companyId, @contractId, @lineNumber,
                    @description, @unit, @customUnit,
                    @quantity, @unitPrice, @totalPrice, NOW()
                );",
                new
                {
                    id = Guid.NewGuid(),
                    companyId = contract.Value.company_id,
                    contractId,
                    lineNumber = nextLineNumber,
                    description = item.Description,
                    unit = item.Unit,
                    customUnit = item.CustomUnit,
                    quantity = item.Quantity,
                    unitPrice = item.UnitPrice,
                    totalPrice = item.TotalPrice
                });
        }

        return new ImportLineItemsResult(
            TotalRows: totalRows,
            SuccessCount: imported.Count,
            ErrorCount: errors.Count,
            Imported: imported,
            Errors: errors);
    }

    /// <summary>
    /// Parses tab- or newline-separated clipboard data and inserts
    /// the rows as new line items. Expected format (one row per
    /// line, columns tab-separated):
    /// <c>line_number\tdescription\tunit\tquantity\tunit_price</c>
    /// or, without a header, the same shape. If the first row's
    /// first cell parses as a header (text, not a number), it's
    /// skipped.
    /// </summary>
    public async Task<ImportLineItemsResult> ImportFromClipboardAsync(Guid contractId, string clipboardData)
    {
        var imported = new List<ImportedLineItem>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(clipboardData))
        {
            errors.Add("البيانات فارغة");
            return new ImportLineItemsResult(0, 0, 1, imported, errors);
        }

        // Split on \r\n or \n, then on \t. Trim and drop empties.
        var lines = clipboardData
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            errors.Add("لا توجد بيانات");
            return new ImportLineItemsResult(0, 0, 1, imported, errors);
        }

        // Detect header: if the first line's first cell is not
        // parseable as a number, treat it as a header.
        var firstCells = lines[0].Split('\t');
        var startIndex = 0;
        if (firstCells.Length > 0 && !decimal.TryParse(firstCells[0].Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out _))
        {
            // First cell is text → header row. The header is just
            // used to disambiguate the column order; we expect
            // description, unit, quantity, unit_price (or with a
            // leading line_number). If we can't tell, assume the
            // standard 5-column shape.
            startIndex = 1;
        }

        var totalRows = 0;
        for (int i = startIndex; i < lines.Count; i++)
        {
            totalRows++;
            var line = lines[i];
            var cells = line.Split('\t');
            var rowNum = i + 1;
            try
            {
                // Try the 5-column shape first (with leading line_number),
                // then 4-column (without). The 4-column form lets users
                // paste straight from Excel's "select description..unit_price"
                // range.
                string description;
                string unit;
                decimal quantity;
                decimal unitPrice;
                string? customUnit = null;
                if (cells.Length >= 5)
                {
                    description = cells[1].Trim();
                    unit = cells[2].Trim().ToLowerInvariant();
                    quantity = ParseDecimal(cells[3]);
                    unitPrice = ParseDecimal(cells[4]);
                    if (cells.Length >= 6) customUnit = cells[5].Trim();
                }
                else if (cells.Length == 4)
                {
                    description = cells[0].Trim();
                    unit = cells[1].Trim().ToLowerInvariant();
                    quantity = ParseDecimal(cells[2]);
                    unitPrice = ParseDecimal(cells[3]);
                }
                else
                {
                    errors.Add($"صف {rowNum}: عدد الأعمدة غير صحيح ({cells.Length})");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(unit))
                {
                    errors.Add($"صف {rowNum}: الوصف أو الوحدة فارغ");
                    continue;
                }
                if (unit == "other" && string.IsNullOrWhiteSpace(customUnit))
                {
                    errors.Add($"صف {rowNum}: custom_unit مطلوب عند unit=other");
                    continue;
                }
                var totalPrice = Math.Round(quantity * unitPrice, 3);
                imported.Add(new ImportedLineItem(
                    LineNumber: totalRows,
                    Description: description,
                    Unit: unit,
                    CustomUnit: unit == "other" ? customUnit : null,
                    Quantity: quantity,
                    UnitPrice: unitPrice,
                    TotalPrice: totalPrice));
            }
            catch (Exception ex)
            {
                errors.Add($"صف {rowNum}: {ex.Message}");
            }
        }

        // Persist the successfully-parsed rows. Same pattern as the
        // Excel path.
        using var conn = _db.CreateConnection();
        var contract = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id)?>(@"
            SELECT id, company_id FROM contracts WHERE id = @id;",
            new { id = contractId });
        if (contract is null)
        {
            errors.Add("العقد غير موجود");
            return new ImportLineItemsResult(totalRows, 0, errors.Count + 1, imported, errors);
        }
        var nextLineNumber = await conn.ExecuteScalarAsync<int>(@"
            SELECT COALESCE(MAX(line_number), 0)
            FROM contract_line_items WHERE contract_id = @contractId;",
            new { contractId });
        foreach (var item in imported)
        {
            nextLineNumber++;
            await conn.ExecuteAsync(@"
                INSERT INTO contract_line_items (
                    id, company_id, contract_id, line_number,
                    description, unit, custom_unit,
                    quantity, unit_price, total_price, created_at
                )
                VALUES (
                    @id, @companyId, @contractId, @lineNumber,
                    @description, @unit, @customUnit,
                    @quantity, @unitPrice, @totalPrice, NOW()
                );",
                new
                {
                    id = Guid.NewGuid(),
                    companyId = contract.Value.company_id,
                    contractId,
                    lineNumber = nextLineNumber,
                    description = item.Description,
                    unit = item.Unit,
                    customUnit = item.CustomUnit,
                    quantity = item.Quantity,
                    unitPrice = item.UnitPrice,
                    totalPrice = item.TotalPrice
                });
        }
        return new ImportLineItemsResult(
            TotalRows: totalRows,
            SuccessCount: imported.Count,
            ErrorCount: errors.Count,
            Imported: imported,
            Errors: errors);
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>
    /// Re-packs line numbers for a contract into 1..N contiguous
    /// values, preserving the existing order. Used after a delete
    /// to keep the numbering clean. Idempotent.
    /// </summary>
    private async Task RepackLineNumbersAsync(Guid contractId)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var ids = (await conn.QueryAsync<Guid>(@"
                SELECT id FROM contract_line_items
                WHERE contract_id = @contractId
                ORDER BY line_number ASC;",
                new { contractId }, tx)).ToList();
            // Two-pass update to dodge the UNIQUE constraint.
            const int TempOffset = 100000;
            for (int i = 0; i < ids.Count; i++)
            {
                await conn.ExecuteAsync(@"
                    UPDATE contract_line_items
                    SET line_number = @temp
                    WHERE id = @id;",
                    new { temp = TempOffset + i + 1, id = ids[i] }, tx);
            }
            for (int i = 0; i < ids.Count; i++)
            {
                await conn.ExecuteAsync(@"
                    UPDATE contract_line_items
                    SET line_number = @final
                    WHERE id = @id;",
                    new { final = i + 1, id = ids[i] }, tx);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Validates the create/update input. Throws
    /// InvalidOperationException with a clear Arabic message on
    /// any rule violation.
    /// </summary>
    private static void ValidateLineItemInput(
        string description, string unit, string? customUnit,
        decimal quantity, decimal unitPrice)
    {
        ValidateUnit(unit, customUnit);
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("وصف البند مطلوب");
        if (quantity < 0)
            throw new InvalidOperationException("الكمية يجب أن تكون أكبر من أو تساوي صفر");
        if (unitPrice < 0)
            throw new InvalidOperationException("سعر الوحدة يجب أن يكون أكبر من أو يساوي صفر");
    }

    /// <summary>
    /// Public unit validator — used by <see cref="VariationService"/>
    /// too, so the allowed-units list has a single source of truth.
    /// </summary>
    public static void ValidateUnit(string unit, string? customUnit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            throw new InvalidOperationException("وحدة البند مطلوبة");
        if (!AllowedUnits.Contains(unit))
            throw new InvalidOperationException(
                $"وحدة غير معروفة: {unit}. المتوقع: m3, m2, m, ton, kg, piece, lump, hour, day, other");
        if (unit.Equals("other", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(customUnit))
            throw new InvalidOperationException("يجب تحديد الوحدة المخصصة (custom_unit) عند اختيار 'other'");
    }

    /// <summary>
    /// Maps a header row to a column-index dictionary. Header
    /// names are matched case-insensitively and we accept a few
    /// common aliases (Arabic and English) so users don't have to
    /// match exactly.
    /// </summary>
    private static Dictionary<string, int> MapHeaders(List<string> headers)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
        {
            var h = headers[i].Trim().ToLowerInvariant();
            // Normalize a few common variants.
            if (h is "line" or "line #" or "line no" or "no" or "م" or "رقم" or "line_number")
                map["line_number"] = i;
            else if (h is "description" or "desc" or "الوصف" or "بند" or "item")
                map["description"] = i;
            else if (h is "unit" or "الوحدة" or "وحدة")
                map["unit"] = i;
            else if (h is "custom_unit" or "custom unit" or "الوحدة المخصصة")
                map["custom_unit"] = i;
            else if (h is "quantity" or "qty" or "الكمية" or "كمية")
                map["quantity"] = i;
            else if (h is "unit_price" or "price" or "سعر الوحدة" or "السعر")
                map["unit_price"] = i;
        }
        return map;
    }

    private static string GetCellString(IXLCell cell)
    {
        if (cell is null) return string.Empty;
        return cell.GetString().Trim();
    }

    private static decimal GetCellDecimal(IXLCell cell)
    {
        if (cell is null) return 0m;
        // ClosedXML can return numeric or text; try both.
        if (cell.DataType == XLDataType.Number)
            return Convert.ToDecimal(cell.GetDouble(), CultureInfo.InvariantCulture);
        var s = cell.GetString().Trim();
        return ParseDecimal(s);
    }

    private static decimal ParseDecimal(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        // Accept both "1,000.5" (English) and "1.000,5" (Arabic/EU) styles.
        var normalized = s.Replace(",", ".");
        // If there are multiple dots, treat the last one as decimal sep.
        var lastDot = normalized.LastIndexOf('.');
        if (lastDot >= 0)
        {
            var intPart = normalized[..lastDot].Replace(".", "");
            var decPart = normalized[(lastDot + 1)..];
            normalized = $"{intPart}.{decPart}";
        }
        if (!decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            throw new InvalidOperationException($"قيمة غير رقمية: '{s}'");
        return v;
    }

    private static ContractLineItemDto MapRow(LineItemRow r) => new(
        r.id, r.company_id, r.contract_id, r.line_number,
        r.description, r.unit, r.custom_unit,
        r.quantity, r.unit_price, r.total_price,
        QuantityBilledSoFar: r.quantity_billed,
        QuantityRemaining: Math.Max(0, r.quantity - r.quantity_billed),
        r.notes, r.created_at, r.updated_at);

    private record LineItemRow(
        Guid id, Guid company_id, Guid contract_id,
        int line_number, string description, string unit, string? custom_unit,
        decimal quantity, decimal unit_price, decimal total_price,
        string? notes, DateTime created_at, DateTime? updated_at,
        decimal quantity_billed);
}
