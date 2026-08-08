using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 38 — Contract Variation (أمر تغيير) service.
///
/// <para>
/// A variation captures an out-of-band scope change on a contract.
/// Each variation has a status:
/// <list type="bullet">
///   <item><b>DRAFT</b> — being assembled; items can be added/edited/removed.</item>
///   <item><b>APPROVED</b> — effective contract value includes this variation's net items.</item>
///   <item><b>REJECTED</b> — archived; no accounting effect.</item>
/// </list>
/// </para>
///
/// <para>
/// The effective contract value (used by billings to compute
/// <c>work_completed_percent</c>) is:
/// <c>contract.contract_value + Σ(approved item.total_price where is_addition=true)
///                            - Σ(approved item.total_price where is_addition=false)</c>
/// </para>
/// </summary>
public class VariationService
{
    private readonly IDbConnectionFactory _db;
    private readonly LineItemService _lineItems;
    private readonly ILogger<VariationService> _log;

    public VariationService(
        IDbConnectionFactory db,
        LineItemService lineItems,
        ILogger<VariationService> log)
    {
        _db = db;
        _lineItems = lineItems;
        _log = log;
    }

    // ============================================================
    // Reads
    // ============================================================

    /// <summary>
    /// Lists all variations for a contract, ordered by
    /// variation_number ascending. Each DTO includes the items
    /// list so the UI can render a full variation view in one
    /// round-trip.
    /// </summary>
    public async Task<List<ContractVariationDto>> GetByContractAsync(Guid contractId)
    {
        using var conn = _db.CreateConnection();
        var variations = (await conn.QueryAsync<VariationRow>(@"
            SELECT id, company_id, contract_id, variation_number,
                   description, variation_date, status, approved_at, approved_by,
                   notes, created_at, updated_at
            FROM contract_variations
            WHERE contract_id = @contractId
            ORDER BY variation_number ASC;",
            new { contractId })).ToList();

        if (variations.Count == 0)
            return new List<ContractVariationDto>();

        var ids = variations.Select(v => v.id).ToList();
        var itemsByVariation = (await conn.QueryAsync<VariationItemRow>(@"
            SELECT id, variation_id, line_number, description, unit, custom_unit,
                   quantity, unit_price, total_price, is_addition, notes
            FROM contract_variation_items
            WHERE variation_id = ANY(@ids)
            ORDER BY variation_id, line_number ASC;",
                new { ids = ids.ToArray() }))
            .GroupBy(i => i.variation_id)
            .ToDictionary(g => g.Key, g => g.Select(MapVariationItem).ToList());

        return variations.Select(v => MapVariation(v,
            itemsByVariation.TryGetValue(v.id, out var its) ? its : new())).ToList();
    }

    /// <summary>
    /// Loads a single variation by id, with its items array.
    /// </summary>
    public async Task<ContractVariationDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var v = await conn.QuerySingleOrDefaultAsync<VariationRow>(@"
            SELECT id, company_id, contract_id, variation_number,
                   description, variation_date, status, approved_at, approved_by,
                   notes, created_at, updated_at
            FROM contract_variations WHERE id = @id;",
            new { id });
        if (v is null) return null;
        var items = (await conn.QueryAsync<VariationItemRow>(@"
            SELECT id, variation_id, line_number, description, unit, custom_unit,
                   quantity, unit_price, total_price, is_addition, notes
            FROM contract_variation_items
            WHERE variation_id = @id
            ORDER BY line_number ASC;",
                new { id }))
            .Select(MapVariationItem)
            .ToList();
        return MapVariation(v, items);
    }

    /// <summary>
    /// Computes the effective contract value: original contract
    /// value plus the net of all APPROVED variation items. This
    /// is what billings use to compute <c>work_completed_percent</c>.
    /// </summary>
    public async Task<decimal> GetEffectiveContractValueAsync(Guid contractId)
    {
        using var conn = _db.CreateConnection();
        var contract = await conn.QuerySingleOrDefaultAsync<decimal?>(@"
            SELECT contract_value FROM contracts WHERE id = @id;",
            new { id = contractId });
        if (contract is null) return 0m;

        var additions = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(vi.total_price), 0)
            FROM contract_variation_items vi
            JOIN contract_variations v ON v.id = vi.variation_id
            WHERE v.contract_id = @contractId
              AND v.status = 'APPROVED'
              AND vi.is_addition = true;",
            new { contractId }) ?? 0m;
        var subtractions = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(vi.total_price), 0)
            FROM contract_variation_items vi
            JOIN contract_variations v ON v.id = vi.variation_id
            WHERE v.contract_id = @contractId
              AND v.status = 'APPROVED'
              AND vi.is_addition = false;",
            new { contractId }) ?? 0m;
        return contract.Value + additions - subtractions;
    }

    /// <summary>
    /// Returns the effective-value breakdown for an endpoint
    /// response: contract_value, the net variation adjustment,
    /// the final effective value, and the count of approved
    /// variations. Returns null if the contract doesn't exist.
    /// </summary>
    public async Task<EffectiveContractValueResponse?> GetEffectiveValueResponseAsync(Guid contractId)
    {
        using var conn = _db.CreateConnection();
        var contract = await conn.QuerySingleOrDefaultAsync<(Guid id, decimal contract_value)?>(@"
            SELECT id, contract_value FROM contracts WHERE id = @id;",
            new { id = contractId });
        if (contract is null) return null;

        var additions = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(vi.total_price), 0)
            FROM contract_variation_items vi
            JOIN contract_variations v ON v.id = vi.variation_id
            WHERE v.contract_id = @contractId
              AND v.status = 'APPROVED'
              AND vi.is_addition = true;",
            new { contractId }) ?? 0m;
        var subtractions = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(vi.total_price), 0)
            FROM contract_variation_items vi
            JOIN contract_variations v ON v.id = vi.variation_id
            WHERE v.contract_id = @contractId
              AND v.status = 'APPROVED'
              AND vi.is_addition = false;",
            new { contractId }) ?? 0m;
        var approvedCount = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM contract_variations
            WHERE contract_id = @contractId AND status = 'APPROVED';",
            new { contractId });
        return new EffectiveContractValueResponse(
            ContractId: contractId,
            ContractValue: contract.Value.contract_value,
            ApprovedVariationsNet: additions - subtractions,
            EffectiveValue: contract.Value.contract_value + additions - subtractions,
            ApprovedVariationCount: approvedCount);
    }

    // ============================================================
    // Create + lifecycle
    // ============================================================

    /// <summary>
    /// Creates a new variation in DRAFT status. The variation
    /// number is auto-assigned (max + 1 within the contract).
    /// Items are added separately via <see cref="AddItemAsync"/>.
    /// </summary>
    public async Task<ContractVariationDto> CreateAsync(Guid contractId, CreateVariationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Description))
            throw new InvalidOperationException("وصف أمر التغيير مطلوب");

        using var conn = _db.CreateConnection();
        var contract = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id)?>(@"
            SELECT id, company_id FROM contracts WHERE id = @id;",
            new { id = contractId });
        if (contract is null)
            throw new InvalidOperationException("العقد غير موجود");

        var nextNumber = await conn.ExecuteScalarAsync<int>(@"
            SELECT COALESCE(MAX(variation_number), 0) + 1
            FROM contract_variations WHERE contract_id = @contractId;",
            new { contractId });

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO contract_variations (
                id, company_id, contract_id, variation_number,
                description, variation_date, status, notes, created_at
            )
            VALUES (
                @id, @companyId, @contractId, @variationNumber,
                @description, @variationDate, 'DRAFT', @notes, NOW()
            );",
            new
            {
                id,
                companyId = contract.Value.company_id,
                contractId,
                variationNumber = nextNumber,
                description = req.Description,
                variationDate = req.VariationDate,
                notes = req.Notes
            });
        return (await GetByIdAsync(id))!;
    }

    /// <summary>
    /// Adds a new item to a DRAFT variation. The line number is
    /// auto-assigned. Refuses if the variation is not in DRAFT
    /// (approved/rejected variations are frozen for accounting).
    /// </summary>
    public async Task<ContractVariationItemDto> AddItemAsync(Guid variationId, AddVariationItemRequest req)
    {
        LineItemService.ValidateUnit(req.Unit, req.CustomUnit);
        if (req.Quantity < 0)
            throw new InvalidOperationException("الكمية يجب أن تكون أكبر من أو تساوي صفر");
        if (req.UnitPrice < 0)
            throw new InvalidOperationException("سعر الوحدة يجب أن يكون أكبر من أو يساوي صفر");
        if (string.IsNullOrWhiteSpace(req.Description))
            throw new InvalidOperationException("وصف البند مطلوب");

        using var conn = _db.CreateConnection();
        var variation = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id, string status)?>(@"
            SELECT id, company_id, status FROM contract_variations WHERE id = @id;",
            new { id = variationId });
        if (variation is null)
            throw new InvalidOperationException("أمر التغيير غير موجود");
        if (variation.Value.status != "DRAFT")
            throw new InvalidOperationException(
                $"لا يمكن إضافة بنود لأمر تغيير بحالة '{variation.Value.status}'. المتوقع: DRAFT");

        var nextLineNumber = await conn.ExecuteScalarAsync<int>(@"
            SELECT COALESCE(MAX(line_number), 0) + 1
            FROM contract_variation_items WHERE variation_id = @variationId;",
            new { variationId });

        var id = Guid.NewGuid();
        var totalPrice = Math.Round(req.Quantity * req.UnitPrice, 3);
        await conn.ExecuteAsync(@"
            INSERT INTO contract_variation_items (
                id, company_id, variation_id, line_number,
                description, unit, custom_unit,
                quantity, unit_price, total_price,
                is_addition, notes, created_at
            )
            VALUES (
                @id, @companyId, @variationId, @lineNumber,
                @description, @unit, @customUnit,
                @quantity, @unitPrice, @totalPrice,
                @isAddition, @notes, NOW()
            );",
            new
            {
                id,
                companyId = variation.Value.company_id,
                variationId,
                lineNumber = nextLineNumber,
                description = req.Description,
                unit = req.Unit,
                customUnit = req.Unit == "other" ? req.CustomUnit : null,
                quantity = req.Quantity,
                unitPrice = req.UnitPrice,
                totalPrice,
                isAddition = req.IsAddition,
                notes = req.Notes
            });

        await TouchVariationAsync(variationId);
        return (await GetItemAsync(id))!;
    }

    /// <summary>
    /// Updates a variation item. Refuses if the parent variation
    /// is not DRAFT.
    /// </summary>
    public async Task<ContractVariationItemDto?> UpdateItemAsync(Guid itemId, UpdateVariationItemRequest req)
    {
        LineItemService.ValidateUnit(req.Unit, req.CustomUnit);
        if (req.Quantity < 0)
            throw new InvalidOperationException("الكمية يجب أن تكون أكبر من أو تساوي صفر");
        if (req.UnitPrice < 0)
            throw new InvalidOperationException("سعر الوحدة يجب أن يكون أكبر من أو يساوي صفر");
        if (string.IsNullOrWhiteSpace(req.Description))
            throw new InvalidOperationException("وصف البند مطلوب");

        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid variation_id, string status)?>(@"
            SELECT vi.id, vi.variation_id, v.status
            FROM contract_variation_items vi
            JOIN contract_variations v ON v.id = vi.variation_id
            WHERE vi.id = @id;",
            new { id = itemId });
        if (existing is null) return null;
        if (existing.Value.status != "DRAFT")
            throw new InvalidOperationException(
                $"لا يمكن تعديل بند أمر تغيير بحالة '{existing.Value.status}'. المتوقع: DRAFT");

        var totalPrice = Math.Round(req.Quantity * req.UnitPrice, 3);
        await conn.ExecuteAsync(@"
            UPDATE contract_variation_items
            SET description = @description,
                unit = @unit,
                custom_unit = @customUnit,
                quantity = @quantity,
                unit_price = @unitPrice,
                total_price = @totalPrice,
                is_addition = @isAddition,
                notes = @notes
            WHERE id = @id;",
            new
            {
                id = itemId,
                description = req.Description,
                unit = req.Unit,
                customUnit = req.Unit == "other" ? req.CustomUnit : null,
                quantity = req.Quantity,
                unitPrice = req.UnitPrice,
                totalPrice,
                isAddition = req.IsAddition,
                notes = req.Notes
            });

        await TouchVariationAsync(existing.Value.variation_id);
        return await GetItemAsync(itemId);
    }

    /// <summary>
    /// Removes an item from a DRAFT variation. Refuses if the
    /// variation is not in DRAFT.
    /// </summary>
    public async Task<bool> RemoveItemAsync(Guid itemId)
    {
        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid variation_id, string status)?>(@"
            SELECT vi.id, vi.variation_id, v.status
            FROM contract_variation_items vi
            JOIN contract_variations v ON v.id = vi.variation_id
            WHERE vi.id = @id;",
            new { id = itemId });
        if (existing is null) return false;
        if (existing.Value.status != "DRAFT")
            throw new InvalidOperationException(
                $"لا يمكن حذف بند أمر تغيير بحالة '{existing.Value.status}'. المتوقع: DRAFT");

        var rows = await conn.ExecuteAsync(
            "DELETE FROM contract_variation_items WHERE id = @id;",
            new { id = itemId });
        if (rows == 0) return false;
        await TouchVariationAsync(existing.Value.variation_id);
        return true;
    }

    /// <summary>
    /// Approves a DRAFT variation: stamps approved_at, approved_by,
    /// sets status='APPROVED'. Once approved, the variation's net
    /// items start contributing to the effective contract value
    /// (used by subsequent billings).
    /// </summary>
    public async Task<ContractVariationDto> ApproveAsync(Guid variationId, Guid userId, ApproveVariationRequest req)
    {
        using var conn = _db.CreateConnection();
        var v = await conn.QuerySingleOrDefaultAsync<(Guid id, string status, DateTime variation_date)?>(@"
            SELECT id, status, variation_date FROM contract_variations WHERE id = @id;",
            new { id = variationId });
        if (v is null)
            throw new InvalidOperationException("أمر التغيير غير موجود");
        if (v.Value.status != "DRAFT")
            throw new InvalidOperationException(
                $"لا يمكن اعتماد أمر تغيير بحالة '{v.Value.status}'. المتوقع: DRAFT");

        // Refuse if the variation has no items — a no-op variation
        // is almost always a mistake (the user forgot to add
        // anything). Require at least one item to approve.
        var hasItems = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM contract_variation_items WHERE variation_id = @id;",
            new { id = variationId });
        if (hasItems == 0)
            throw new InvalidOperationException(
                "لا يمكن اعتماد أمر تغيير بدون بنود");

        var approvedAt = req.ApprovedAt ?? DateTime.UtcNow;
        await conn.ExecuteAsync(@"
            UPDATE contract_variations
            SET status = 'APPROVED',
                approved_at = @approvedAt,
                approved_by = @approvedBy,
                updated_at = NOW()
            WHERE id = @id;",
            new { id = variationId, approvedAt, approvedBy = userId });

        _log.LogInformation("Variation {Id} approved by user {UserId} at {At}",
            variationId, userId, approvedAt);
        return (await GetByIdAsync(variationId))!;
    }

    /// <summary>
    /// Rejects a DRAFT variation: sets status='REJECTED'. The
    /// variation is archived and never affects the effective
    /// contract value. Items are NOT deleted — the rejection
    /// audit trail is preserved.
    /// </summary>
    public async Task<ContractVariationDto> RejectAsync(Guid variationId)
    {
        using var conn = _db.CreateConnection();
        var v = await conn.QuerySingleOrDefaultAsync<(Guid id, string status)?>(@"
            SELECT id, status FROM contract_variations WHERE id = @id;",
            new { id = variationId });
        if (v is null)
            throw new InvalidOperationException("أمر التغيير غير موجود");
        if (v.Value.status != "DRAFT")
            throw new InvalidOperationException(
                $"لا يمكن رفض أمر تغيير بحالة '{v.Value.status}'. المتوقع: DRAFT");
        await conn.ExecuteAsync(@"
            UPDATE contract_variations
            SET status = 'REJECTED', updated_at = NOW()
            WHERE id = @id;",
            new { id = variationId });
        return (await GetByIdAsync(variationId))!;
    }

    // ============================================================
    // Helpers
    // ============================================================

    private async Task<ContractVariationItemDto?> GetItemAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<VariationItemRow>(@"
            SELECT id, variation_id, line_number, description, unit, custom_unit,
                   quantity, unit_price, total_price, is_addition, notes
            FROM contract_variation_items WHERE id = @id;",
            new { id });
        return row is null ? null : MapVariationItem(row);
    }

    /// <summary>
    /// Bumps the variation's <c>updated_at</c> after a child
    /// change. Surfaces the modification time in the UI without
    /// requiring a separate "last item change" column.
    /// </summary>
    private async Task TouchVariationAsync(Guid variationId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE contract_variations SET updated_at = NOW() WHERE id = @id;",
            new { id = variationId });
    }

    private static ContractVariationDto MapVariation(VariationRow v, List<ContractVariationItemDto> items) =>
        new(
            v.id, v.company_id, v.contract_id, v.variation_number,
            v.description, v.variation_date, v.status,
            v.approved_at, v.approved_by, v.notes,
            v.created_at, v.updated_at, items);

    private static ContractVariationItemDto MapVariationItem(VariationItemRow r) => new(
        r.id, r.variation_id, r.line_number, r.description,
        r.unit, r.custom_unit, r.quantity, r.unit_price, r.total_price,
        r.is_addition, r.notes);

    private record VariationRow(
        Guid id, Guid company_id, Guid contract_id,
        int variation_number, string description, DateTime variation_date,
        string status, DateTime? approved_at, Guid? approved_by,
        string? notes, DateTime created_at, DateTime? updated_at);

    private record VariationItemRow(
        Guid id, Guid variation_id, int line_number, string description,
        string unit, string? custom_unit,
        decimal quantity, decimal unit_price, decimal total_price,
        bool is_addition, string? notes);
}
