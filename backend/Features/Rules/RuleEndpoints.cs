using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Rules;

public static class RuleEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/rules").WithTags("Rules").RequireAuthorization();

        grp.MapGet("/", async ([FromQuery] bool? templates, RuleService svc) =>
        {
            var data = await svc.GetAllAsync(templates);
            return Results.Ok(data);
        });

        grp.MapGet("/{id:guid}", async (Guid id, RuleService svc) =>
        {
            var r = await svc.GetByIdAsync(id);
            return r is null ? Results.NotFound() : Results.Ok(r);
        });

        grp.MapPost("/", async ([FromBody] CreateRuleRequest req, RuleService svc) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.EventName))
                    return Results.BadRequest(new { error = "Name and EventName required" });
                var r = await svc.CreateAsync(req);
                return Results.Created($"/api/rules/{r.Id}", r);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] UpdateRuleRequest req, RuleService svc) =>
        {
            var r = await svc.UpdateAsync(id, req);
            return r is null ? Results.NotFound() : Results.Ok(r);
        });

        grp.MapDelete("/{id:guid}", async (Guid id, RuleService svc) =>
        {
            var ok = await svc.DeleteAsync(id);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // Test/trigger an event
        grp.MapPost("/trigger", async ([FromBody] TriggerEventRequest req, RuleEvaluator evaluator, HttpContext ctx) =>
        {
            var companyId = ctx.GetActiveCompanyIdFromHeader();
            if (companyId is null) return Results.BadRequest(new { error = "X-Company-Id header required" });
            var userId = ctx.GetUserId();
            try
            {
                var entries = await evaluator.TriggerEventAsync(companyId.Value, userId, req.EventName, req.Payload);
                return Results.Ok(new { triggered = entries.Count, entries });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
