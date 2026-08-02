using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Rules;

public class RuleService
{
    private readonly IDbConnectionFactory _db;

    public RuleService(IDbConnectionFactory db) => _db = db;

    public async Task<List<RuleDto>> GetAllAsync(bool? isTemplate = null)
    {
        using var conn = _db.CreateConnection();
        var sql = isTemplate is null
            ? @"SELECT id, name, description, event_name, enabled, priority, rule_json::text, is_template, created_at, updated_at
                FROM business_rules ORDER BY priority, name;"
            : @"SELECT id, name, description, event_name, enabled, priority, rule_json::text, is_template, created_at, updated_at
                FROM business_rules WHERE is_template = @isTemplate ORDER BY priority, name;";
        var rows = await conn.QueryAsync<RuleRow>(sql, new { isTemplate });
        return rows.Select(Map).ToList();
    }

    public async Task<RuleDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<RuleRow>(@"
            SELECT id, name, description, event_name, enabled, priority, rule_json::text, is_template, created_at, updated_at
            FROM business_rules WHERE id = @id;",
            new { id });
        return row is null ? null : Map(row);
    }

    public async Task<RuleDto> CreateAsync(CreateRuleRequest req)
    {
        using var conn = _db.CreateConnection();
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO business_rules (id, name, description, event_name, enabled, priority, rule_json, is_template)
            VALUES (@id, @name, @description, @eventName, @enabled, @priority, @ruleJson::jsonb, false);",
            new
            {
                id,
                name = req.Name,
                description = req.Description,
                eventName = req.EventName,
                enabled = req.Enabled,
                priority = req.Priority,
                ruleJson = req.RuleJson
            });
        return (await GetByIdAsync(id))!;
    }

    public async Task<RuleDto?> UpdateAsync(Guid id, UpdateRuleRequest req)
    {
        using var conn = _db.CreateConnection();
        var rowsAffected = await conn.ExecuteAsync(@"
            UPDATE business_rules
            SET name = @name, description = @description, event_name = @eventName,
                enabled = @enabled, priority = @priority, rule_json = @ruleJson::jsonb,
                updated_at = NOW()
            WHERE id = @id;",
            new
            {
                id,
                name = req.Name,
                description = req.Description,
                eventName = req.EventName,
                enabled = req.Enabled,
                priority = req.Priority,
                ruleJson = req.RuleJson
            });
        return rowsAffected == 0 ? null : await GetByIdAsync(id);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var rowsAffected = await conn.ExecuteAsync(
            "DELETE FROM business_rules WHERE id = @id;",
            new { id });
        return rowsAffected > 0;
    }

    private static RuleDto Map(RuleRow r) => new(
        r.id, r.name, r.description, r.event_name, r.enabled, r.priority,
        r.rule_json, r.is_template, r.created_at, r.updated_at);

    private record RuleRow(
        Guid id, string name, string? description, string event_name, bool enabled,
        int priority, string rule_json, bool is_template, DateTime created_at, DateTime updated_at);
}
