using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Contacts;

public static class ContactEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/contacts").WithTags("Contacts").RequireAuthorization();

        grp.MapGet("/", async (
            [FromQuery] Guid companyId,
            [FromQuery] string? type,
            [FromQuery] bool? includeInactive,
            [FromServices] ContactService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var data = await svc.GetByCompanyAsync(companyId, type, includeInactive ?? false);
            return Results.Ok(data);
        });

        grp.MapGet("/{id:guid}", async (Guid id, [FromServices] ContactService svc) =>
        {
            var c = await svc.GetByIdAsync(id);
            return c is null ? Results.NotFound() : Results.Ok(c);
        });

        grp.MapPost("/", async ([FromBody] CreateContactRequest req, [FromServices] ContactService svc) =>
        {
            try
            {
                var c = await svc.CreateAsync(req);
                return Results.Created($"/api/contacts/{c.Id}", c);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] UpdateContactRequest req, [FromServices] ContactService svc) =>
        {
            try
            {
                var c = await svc.UpdateAsync(id, req);
                return c is null ? Results.NotFound() : Results.Ok(c);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapDelete("/{id:guid}", async (Guid id, [FromServices] ContactService svc) =>
        {
            var ok = await svc.DeleteAsync(id);
            return ok ? Results.NoContent() : Results.NotFound();
        });
    }
}
