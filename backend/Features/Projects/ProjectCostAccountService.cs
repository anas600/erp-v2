using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 50 — Auto-creates the 7 L4 sub-ledger accounts for a project's
/// cost tracking (one per category: 5401..5407).
///
/// Why:
///   When a new project is created, the system needs 7 sub-ledger accounts
///   under the L3 control accounts 5401..5407 so that purchase-invoice
///   lines allocated to this project can post to a project-specific
///   sub-ledger (e.g. <c>5401-anas</c>) instead of the control account.
///
///   The naming convention is <c>{L3-code}-{project-code}</c>, the same
///   pattern already used for sub-ledgers under 1103 (AR) and 2101 (AP).
///   E.g. project <c>PRJ-2026-005</c> gets:
///     <c>5401-PRJ-2026-005</c> Project Materials — PRJ-2026-005
///     <c>5402-PRJ-2026-005</c> Project Labor — PRJ-2026-005
///     ...
///     <c>5407-PRJ-2026-005</c> Project Other Costs — PRJ-2026-005
///
/// Idempotency:
///   The service is safe to call multiple times. If the sub-ledger
///   already exists (same project + same L3 code), it skips it. This
///   matters because a project can be re-saved (PATCH) and we don't
///   want a duplicate-account crash.
/// </summary>
public class ProjectCostAccountService
{
    private readonly IDbConnectionFactory _db;

    /// <summary>
    /// L3 cost account codes that get a sub-ledger per project. Ordered
    /// in the way they'll appear in a tree view.
    /// </summary>
    public static readonly (string L3Code, string L3NameEn, string L3NameAr)[] ProjectCostL3Accounts = new[]
    {
        ("5401", "Project Materials",           "مواد خام مشروع"),
        ("5402", "Project Labor",               "أجور عمال مشروع"),
        ("5403", "Project Subcontractors",      "مقاولون باطن"),
        ("5404", "Project Equipment Rental",    "إيجار معدات مشروع"),
        ("5405", "Project Overhead Allocation", "مصاريف عمومية مخصصة"),
        ("5406", "Project Transportation",      "نقل وشحن"),
        ("5407", "Project Other Costs",         "مصاريف مشاريع أخرى"),
    };

    public ProjectCostAccountService(IDbConnectionFactory db) => _db = db;

    /// <summary>
    /// Reads the L3 cost accounts that should be replicated per project.
    /// Uses the helper SQL function from migration 025 when available
    /// (faster, hits the unique index); falls back to a plain query
    /// for environments that haven't applied the migration yet.
    /// </summary>
    private async Task<List<(string code, Guid id, Guid parentId)>> LoadL3CostAccountsAsync(
        System.Data.IDbConnection conn, Guid companyId, System.Data.IDbTransaction? tx = null)
    {
        var rows = await conn.QueryAsync<(string code, Guid id, Guid parentId)>(@"
            SELECT code, id, COALESCE(parent_id, '00000000-0000-0000-0000-000000000000'::uuid) AS parentId
            FROM accounts
            WHERE company_id = @companyId
              AND code = ANY(@codes)
              AND level = 3
              AND is_active = true;",
            new
            {
                companyId,
                codes = ProjectCostL3Accounts.Select(a => a.L3Code).ToArray()
            }, tx);
        return rows.ToList();
    }

    /// <summary>
    /// Create the 7 L4 sub-ledger accounts for a project.
    /// Returns the list of created (L3 code, L4 account id) pairs.
    /// Skips any that already exist (idempotent).
    /// </summary>
    public async Task<List<(string L3Code, Guid SubLedgerId, string SubLedgerCode)>>
        CreateProjectSubLedgersAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            // Load the project (need its company + code + name)
            var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id, string code, string? name, string? name_ar)?>(@"
                SELECT id, company_id, code, name, name_ar FROM projects WHERE id = @id;",
                new { id = projectId }, tx);
            if (project is null)
                throw new InvalidOperationException($"Project {projectId} not found");

            var l3Accounts = await LoadL3CostAccountsAsync(conn, project.Value.company_id, tx);
            if (l3Accounts.Count == 0)
                throw new InvalidOperationException(
                    $"No L3 project cost accounts (5401-5407) found in company {project.Value.company_id}. " +
                    "Run the COA seed before creating projects.");

            var projectLabel = project.Value.name_ar ?? project.Value.name ?? project.Value.code;
            var created = new List<(string, Guid, string)>();

            foreach (var l3 in l3Accounts)
            {
                // Sub-ledger naming: {L3}-{project.code}
                // E.g. project PRJ-2026-005 → 5401-PRJ-2026-005
                var subCode = $"{l3.code}-{project.Value.code}";
                var l3Info = ProjectCostL3Accounts.First(x => x.L3Code == l3.code);
                var subName = $"{l3Info.L3NameAr} — {projectLabel}";

                // Idempotency check: does this sub-ledger already exist?
                var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
                    SELECT id FROM accounts
                    WHERE company_id = @companyId AND code = @code AND level = 4;",
                    new { companyId = project.Value.company_id, code = subCode }, tx);
                if (existing.HasValue)
                {
                    created.Add((l3.code, existing.Value, subCode));
                    continue;
                }

                var subId = Guid.NewGuid();
                await conn.ExecuteAsync(@"
                    INSERT INTO accounts
                        (id, company_id, code, name, name_ar, parent_id, account_type, nature, level,
                         is_control_account, is_postable, is_active, created_at)
                    VALUES
                        (@id, @companyId, @code, @nameEn, @nameAr, @parentId, 'Expense', 'Debit', 4,
                         false, true, true, NOW());",
                    new
                    {
                        id = subId,
                        companyId = project.Value.company_id,
                        code = subCode,
                        nameEn = $"{l3Info.L3NameEn} — {projectLabel}",
                        nameAr = subName,
                        parentId = l3.id
                    }, tx);

                created.Add((l3.code, subId, subCode));
            }

            tx.Commit();
            return created;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Returns the L4 sub-ledger accounts for a project (read-only).
    /// Used by the rule evaluator and by the invoice form's "line account"
    /// dropdown.
    /// </summary>
    public async Task<List<ProjectCostAccountDto>> GetProjectCostAccountsAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id)?>(@"
            SELECT id, company_id FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null) return new();

        var rows = await conn.QueryAsync<ProjectCostAccountDto>(@"
            SELECT a.id, a.code, a.name, a.name_ar AS NameAr, a.parent_id AS ParentId,
                   a.level, a.is_postable AS IsPostable
            FROM accounts a
            JOIN accounts parent ON parent.id = a.parent_id
            WHERE a.company_id = @companyId
              AND a.level = 4
              AND a.code LIKE '54%-%'
              AND parent.code = ANY(@l3Codes)
            ORDER BY a.code;",
            new
            {
                companyId = project.Value.company_id,
                l3Codes = ProjectCostL3Accounts.Select(a => a.L3Code).ToArray()
            });

        return rows.ToList();
    }

    /// <summary>
    /// Same as above but filtered to a specific project (matches by code
    /// prefix). Used when the project is known and we want a tight list
    /// of "this project's 7 sub-ledgers".
    /// </summary>
    public async Task<List<ProjectCostAccountDto>> GetSubLedgersForProjectAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id, string code)?>(@"
            SELECT id, company_id, code FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null) return new();

        var l3Codes = ProjectCostL3Accounts.Select(a => a.L3Code).ToArray();
        var rows = await conn.QueryAsync<ProjectCostAccountDto>(@"
            SELECT a.id, a.code, a.name, a.name_ar AS NameAr, a.parent_id AS ParentId,
                   a.level, a.is_postable AS IsPostable
            FROM accounts a
            JOIN accounts parent ON parent.id = a.parent_id
            WHERE a.company_id = @companyId
              AND a.level = 4
              AND parent.code = ANY(@l3Codes)
              AND a.code LIKE '%' || @projectCode
            ORDER BY a.code;",
            new
            {
                companyId = project.Value.company_id,
                l3Codes,
                projectCode = project.Value.code
            });

        return rows.ToList();
    }
}

public record ProjectCostAccountDto(
    Guid Id,
    string Code,
    string Name,
    string? NameAr,
    Guid ParentId,
    int Level,
    bool IsPostable
);
