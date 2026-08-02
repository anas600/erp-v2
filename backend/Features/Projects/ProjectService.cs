using Dapper;
using ErpV2.Common;
using ErpV2.Features.Rules;
using ErpV2.Features.Journal;

namespace ErpV2.Features.Projects;

/// <summary>
/// Project service: manages projects and their milestones.
/// Completing a milestone triggers the "ProjectMilestoneCompleted" event
/// in the rules engine, which (with the default template) creates a journal entry.
/// </summary>
public class ProjectService
{
    private readonly IDbConnectionFactory _db;
    private readonly RuleEvaluator _rules;

    public ProjectService(IDbConnectionFactory db, RuleEvaluator rules)
    {
        _db = db;
        _rules = rules;
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
                   start_date, end_date, budget, actual_cost, notes, created_at, updated_at
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

        return new ProjectDto(
            p.id, p.company_id, p.code, p.name, p.name_ar, p.description, p.status,
            p.start_date, p.end_date, p.budget, p.actual_cost, p.notes, p.created_at, p.updated_at,
            milestones.Select(m => new MilestoneDto(
                m.id, m.project_id, m.name, m.name_ar, m.description, m.amount,
                m.status, m.target_date, m.completed_at, m.order_index
            )).ToList()
        );
    }

    public async Task<ProjectDto> CreateAsync(CreateProjectRequest req)
    {
        using var conn = _db.CreateConnection();
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO projects (id, company_id, code, name, name_ar, description,
                status, start_date, end_date, budget, notes)
            VALUES (@id, @companyId, @code, @name, @nameAr, @description,
                'active', @startDate, @endDate, @budget, @notes);",
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
                notes = req.Notes
            });
        return (await GetByIdAsync(id))!;
    }

    public async Task<ProjectDto?> UpdateAsync(Guid id, UpdateProjectRequest req)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(@"
            UPDATE projects
            SET name = @name, name_ar = @nameAr, description = @description,
                status = @status, start_date = @startDate, end_date = @endDate,
                budget = @budget, notes = @notes, updated_at = NOW()
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
                notes = req.Notes
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

    private record ProjectRow(
        Guid id, Guid company_id, string code, string name, string? name_ar,
        string? description, string status, DateTime? start_date, DateTime? end_date,
        decimal budget, decimal actual_cost, string? notes,
        DateTime created_at, DateTime? updated_at);

    private record MilestoneRow(
        Guid id, Guid project_id, string name, string? name_ar, string? description,
        decimal amount, string status, DateTime? target_date, DateTime? completed_at, int order_index);
}
