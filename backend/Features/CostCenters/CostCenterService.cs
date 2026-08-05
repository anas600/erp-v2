using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.CostCenters;

/// <summary>
/// Manages cost centers for a company. Cost centers let journal
/// lines be tagged with a project/department/activity so reports can
/// break out P&L by dimension.
///
/// Soft-delete only: a cost center referenced by any posted journal
/// line is preserved (is_active flipped to false) rather than hard-
/// deleted, to keep the audit trail intact.
/// </summary>
public class CostCenterService
{
    private readonly IDbConnectionFactory _db;

    public CostCenterService(IDbConnectionFactory db) => _db = db;

    /// <summary>
    /// List all cost centers for a company. Returns active and
    /// inactive (we don't filter on is_active here — the UI wants
    /// to see inactive ones for context, and can filter client-side).
    /// </summary>
    public async Task<List<CostCenterDto>> GetByCompanyAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<CostCenterRow>(@"
            SELECT id, company_id, code, name, name_ar, type, project_id, parent_id,
                   is_active, created_at
            FROM cost_centers
            WHERE company_id = @companyId
            ORDER BY code;",
            new { companyId });
        return rows.Select(Map).ToList();
    }

    public async Task<CostCenterDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<CostCenterRow>(@"
            SELECT id, company_id, code, name, name_ar, type, project_id, parent_id,
                   is_active, created_at
            FROM cost_centers WHERE id = @id;",
            new { id });
        return row is null ? null : Map(row);
    }

    public async Task<CostCenterDto> CreateAsync(CreateCostCenterRequest req)
    {
        // Validate type — must be one of the three recognised kinds.
        // (The DB defaults to 'project' but we validate explicitly so
        // a bad value surfaces as a friendly 400, not a check constraint
        // violation.)
        var validTypes = new[] { "project", "department", "activity" };
        if (string.IsNullOrWhiteSpace(req.Type) || !validTypes.Contains(req.Type))
            throw new ArgumentException($"Type must be one of: {string.Join(", ", validTypes)}");

        if (string.IsNullOrWhiteSpace(req.Code))
            throw new ArgumentException("Code is required");
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ArgumentException("Name is required");

        // If parent_id is provided, ensure it exists and belongs to the same company.
        // Cross-company parenting would let a tenant see another tenant's cost
        // center tree — reject up front.
        if (req.ParentId.HasValue)
        {
            var parent = await GetByIdAsync(req.ParentId.Value);
            if (parent is null) throw new ArgumentException("Parent cost center not found");
            if (parent.CompanyId != req.CompanyId)
                throw new ArgumentException("Parent must be in the same company");
        }

        // If project_id is provided, validate it exists in the same company.
        // (We don't enforce same-company for project_id today because the
        // projects table doesn't have a company_id denormalised in a way we
        // can cheaply join from here — kept as a soft check: just verify
        // the row exists. The UI is the gatekeeper.)
        if (req.ProjectId.HasValue)
        {
            using var checkConn = _db.CreateConnection();
            var projectExists = await checkConn.ExecuteScalarAsync<int?>(@"
                SELECT 1 FROM projects WHERE id = @id LIMIT 1;",
                new { id = req.ProjectId.Value });
            if (projectExists is null) throw new ArgumentException("Project not found");
        }

        using var conn = _db.CreateConnection();
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO cost_centers (
                id, company_id, code, name, name_ar, type, project_id, parent_id,
                is_active, created_at
            )
            VALUES (
                @id, @companyId, @code, @name, @nameAr, @type, @projectId, @parentId,
                true, NOW()
            );",
            new
            {
                id,
                companyId = req.CompanyId,
                code = req.Code,
                name = req.Name,
                nameAr = req.NameAr,
                type = req.Type,
                projectId = req.ProjectId,
                parentId = req.ParentId
            });

        return (await GetByIdAsync(id))!;
    }

    public async Task<CostCenterDto?> UpdateAsync(Guid id, UpdateCostCenterRequest req)
    {
        using var conn = _db.CreateConnection();

        // Load existing so we can fill in unspecified fields (PATCH-like
        // semantics: the caller only sends the fields they want to change,
        // we keep the existing values for the rest).
        var existing = await GetByIdAsync(id);
        if (existing is null) return null;

        var newName = req.Name ?? existing.Name;
        var newNameAr = req.NameAr ?? existing.NameAr;
        var newIsActive = req.IsActive ?? existing.IsActive;

        var rowsAffected = await conn.ExecuteAsync(@"
            UPDATE cost_centers
            SET name = @name, name_ar = @nameAr, is_active = @isActive
            WHERE id = @id;",
            new { id, name = newName, nameAr = newNameAr, isActive = newIsActive });

        return rowsAffected == 0 ? null : await GetByIdAsync(id);
    }

    /// <summary>
    /// Soft-delete a cost center: flips is_active to false rather than
    /// hard-deleting, so historical journal_lines that reference this
    /// cost center remain valid (the FK allows NULL, and the rows
    /// keep the cost_center_id pointing at this cost center).
    ///
    /// Refuses to deactivate if any POSTED journal entry uses this
    /// cost center — flipping is_active to false would hide the cost
    /// center from dropdowns even though posted entries still
    /// reference it, breaking report-by-cost-center for closed periods.
    /// Draft and pending entries do not block (they can be edited to
    /// drop the cost center before posting).
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();

        // Verify the cost center exists.
        var existing = await GetByIdAsync(id);
        if (existing is null) return false;

        // Refuse if any posted journal line references this cost center.
        // "Posted" means status='posted' on the parent journal_entry.
        // We join through journal_entries because journal_lines itself
        // has no status column.
        var postedCount = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*)::int
            FROM journal_lines jl
            JOIN journal_entries je ON je.id = jl.journal_entry_id
            WHERE jl.cost_center_id = @id
              AND je.status = 'posted';",
            new { id });

        if (postedCount > 0)
            throw new InvalidOperationException(
                $"لا يمكن إلغاء تفعيل مركز التكلفة لأنه مستخدم في {postedCount} قيد مرحّل. "
                + "استبدل مركز التكلفة في القيود أولاً أو اعكس القيود.");

        // No posted references — safe to soft-delete.
        await conn.ExecuteAsync(
            "UPDATE cost_centers SET is_active = false WHERE id = @id;",
            new { id });
        return true;
    }

    private static CostCenterDto Map(CostCenterRow r) => new(
        r.id, r.company_id, r.code, r.name, r.name_ar, r.type,
        r.project_id, r.parent_id, r.is_active, r.created_at);

    // Snake-case row record matching the Postgres column names. Dapper
    // maps to it positionally based on the SELECT list.
    private record CostCenterRow(
        Guid id, Guid company_id, string code, string name, string? name_ar,
        string type, Guid? project_id, Guid? parent_id,
        bool is_active, DateTime created_at);
}
